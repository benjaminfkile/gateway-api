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
public sealed record InstanceServiceEntry(
    string Name,
    string? Digest,
    string State,
    DateTimeOffset? StartedAt,
    int Restarts);

/// <summary>
/// Serialises (and parses) this instance's container inventory to/from the
/// <c>services</c> jsonb payload of <c>instance_status</c> (tech-spec §4.4):
/// <c>[{name, digest, state, startedAt, restarts}]</c>. Every reconcile loop
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

    /// <summary>Build the <c>services</c> jsonb string from the managed containers.</summary>
    public static string Build(IReadOnlyList<ContainerInfo> containers)
    {
        var entries = containers
            .Select(c => new InstanceServiceEntry(c.Name, c.Digest, c.State, c.StartedAt, c.Restarts))
            .ToList();

        return JsonSerializer.Serialize(entries, Options);
    }

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
