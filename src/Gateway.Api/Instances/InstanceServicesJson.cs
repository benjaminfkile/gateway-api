using System.Text.Json;
using System.Text.Json.Serialization;
using Gateway.Api.Containers;

namespace Gateway.Api.Instances;

/// <summary>
/// One element of the <c>instance_status.services</c> jsonb array
/// (tech-spec §4.4): a single service's inventory on one instance.
/// </summary>
/// <param name="Name">Canonical service name.</param>
/// <param name="Digest">Image digest the container is running, or null when unknown.</param>
/// <param name="State">Docker container state, e.g. <c>running</c> / <c>exited</c>.</param>
/// <param name="StartedAt">When the container last started, or null when unknown.</param>
/// <param name="Restarts">Docker restart count for the container.</param>
/// <param name="HostPort">
/// The Docker-assigned host port the container is published on, or null when no
/// container (or an older instance that did not record it). Surfaced so the ops
/// dashboard can show where each service is actually bound.
/// </param>
/// <param name="LastError">
/// The failure detail of the most recent reconcile action for this service, or null
/// when the last action succeeded (a success clears it). Trimmed to ~300 chars. This
/// makes convergence failures — e.g. a stale-digest start that loops on
/// <c>No such image</c> — visible through the management API instead of hiding in
/// journald (production 2026-08-08). Null on older rows written before this field
/// existed, so parsing stays backward compatible.
/// </param>
/// <param name="LastErrorAt">When <see cref="LastError"/> was recorded, or null when there is no error.</param>
public sealed record InstanceServiceEntry(
    string Name,
    string? Digest,
    string State,
    DateTimeOffset? StartedAt,
    int Restarts,
    int? HostPort = null,
    string? LastError = null,
    DateTimeOffset? LastErrorAt = null);

/// <summary>
/// The outcome of the most recent reconcile action for one service on this instance
/// when it failed: the (trimmed) failure detail and when it happened. The reconciler
/// tracks one of these per service and clears it on a subsequent success.
/// </summary>
/// <param name="Message">Failure detail, already trimmed to <see cref="InstanceServicesJson.MaxErrorLength"/>.</param>
/// <param name="At">When the failure was recorded.</param>
public sealed record ServiceError(string Message, DateTimeOffset At);

/// <summary>
/// Serialises (and parses) this instance's container inventory to/from the
/// <c>services</c> jsonb payload of <c>instance_status</c> (tech-spec §4.4):
/// <c>[{name, digest, state, startedAt, restarts, hostPort}]</c>. Every reconcile loop
/// rebuilds this from the live managed-container list so any instance can answer
/// fleet-wide queries; the Management API parses it back to compute fleet rollups.
/// </summary>
public static class InstanceServicesJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>State recorded for a service that is desired running but has no container (repeated failed starts).</summary>
    public const string AbsentState = "absent";

    /// <summary>Upper bound on a recorded <see cref="InstanceServiceEntry.LastError"/> length (~300 chars, tech-spec §4.4).</summary>
    public const int MaxErrorLength = 300;

    /// <summary>
    /// Build the <c>services</c> jsonb string from the managed containers, attaching
    /// the most recent per-service reconcile error where one was recorded.
    /// <para>
    /// A service that is <b>absent</b> (no container) but still carries an error —
    /// e.g. a start that keeps failing — is emitted as an <see cref="AbsentState"/>
    /// entry so the failure is visible; without this the repeated failure would be
    /// invisible because there is no container to list (tech-spec §4.4, production
    /// 2026-08-08).
    /// </para>
    /// </summary>
    public static string Build(
        IReadOnlyList<ContainerInfo> containers,
        IReadOnlyDictionary<string, ServiceError>? errors = null)
    {
        var entries = new List<InstanceServiceEntry>(containers.Count);
        var listed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in containers)
        {
            listed.Add(c.Name);
            var error = Lookup(errors, c.Name);
            entries.Add(new InstanceServiceEntry(
                c.Name, c.Digest, c.State, c.StartedAt, c.Restarts, c.HostPort,
                error?.Message, error?.At));
        }

        if (errors is not null)
        {
            foreach (var (name, error) in errors)
            {
                if (listed.Contains(name))
                {
                    continue;
                }

                entries.Add(new InstanceServiceEntry(
                    name, Digest: null, State: AbsentState, StartedAt: null, Restarts: 0,
                    HostPort: null, LastError: error.Message, LastErrorAt: error.At));
            }
        }

        return JsonSerializer.Serialize(entries, Options);
    }

    /// <summary>Trim a failure detail to <see cref="MaxErrorLength"/> for storage in the services JSON.</summary>
    public static string TrimError(string message)
    {
        message = message.Trim();
        return message.Length <= MaxErrorLength ? message : message[..MaxErrorLength];
    }

    private static ServiceError? Lookup(IReadOnlyDictionary<string, ServiceError>? errors, string name) =>
        errors is not null && errors.TryGetValue(name, out var error) ? error : null;

    /// <summary>
    /// Parse the <c>services</c> jsonb string back into entries. Returns an empty
    /// list for null/blank/malformed payloads — the Management API must never fail a
    /// fleet query because one instance wrote an unreadable row.
    /// </summary>
    public static IReadOnlyList<InstanceServiceEntry> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<InstanceServiceEntry>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<InstanceServiceEntry>>(json, Options)
                ?? (IReadOnlyList<InstanceServiceEntry>)Array.Empty<InstanceServiceEntry>();
        }
        catch (JsonException)
        {
            return Array.Empty<InstanceServiceEntry>();
        }
    }
}
