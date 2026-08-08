using Gateway.Api.Data;
using Gateway.Api.Instances;

namespace Gateway.Api.Management;

/// <summary>
/// Fleet rollup for one service, computed from the <c>instance_status</c> rows
/// (tech-spec §4.5): "running on 20/20, digest abc123 on 20/20". Fleet-aware — read
/// from the DB, never from local Docker — so it is identical whichever instance the
/// load balancer routed the dashboard's request to.
/// </summary>
/// <param name="RunningOn">Number of instances currently running the service.</param>
/// <param name="TotalInstances">Fleet size recorded in <c>instance_status</c>.</param>
/// <param name="Digests">Per-digest instance counts among the instances running the service.</param>
/// <param name="HostPort">
/// The Docker-assigned host port the service is published on, taken from a running
/// instance (host ports are per-instance and now dynamic — this is a representative
/// value). Null when no instance is running a container for the service.
/// </param>
/// <param name="ErrorOn">
/// Number of instances whose inventory entry for the service carries a
/// <c>lastError</c> — i.e. the last reconcile action for it failed there. A service
/// failing to start on every instance shows <c>errorOn</c> == fleet size even though
/// <see cref="RunningOn"/> is 0 (tech-spec §4.4, production 2026-08-08).
/// </param>
/// <param name="LatestError">
/// The most recent per-service error across the fleet (by timestamp), or null when
/// no instance reports one.
/// </param>
public sealed record FleetRollup(
    int RunningOn,
    int TotalInstances,
    IReadOnlyDictionary<string, int> Digests,
    int? HostPort,
    int ErrorOn,
    FleetServiceError? LatestError)
{
    /// <summary>Placeholder key for an instance running the service on an unknown digest.</summary>
    public const string UnknownDigest = "unknown";

    /// <summary>
    /// Compute the rollup for <paramref name="service"/> across the given fleet rows.
    /// An instance counts toward <see cref="RunningOn"/> (and its digest bucket) when
    /// its inventory lists the service in the <c>running</c> state; it counts toward
    /// <see cref="ErrorOn"/> when its entry carries a <c>lastError</c> (which can be a
    /// running container that failed to upgrade, or an absent-but-desired one that
    /// keeps failing to start).
    /// </summary>
    public static FleetRollup Compute(string service, IReadOnlyCollection<InstanceStatus> instances)
    {
        var digests = new Dictionary<string, int>(StringComparer.Ordinal);
        var runningOn = 0;
        int? hostPort = null;
        var errorOn = 0;
        FleetServiceError? latestError = null;

        foreach (var instance in instances)
        {
            var entries = InstanceServicesJson.Parse(instance.Services)
                .Where(e => string.Equals(e.Name, service, StringComparison.Ordinal))
                .ToList();

            var running = entries.FirstOrDefault(e =>
                string.Equals(e.State, "running", StringComparison.OrdinalIgnoreCase));
            if (running is not null)
            {
                runningOn++;
                hostPort ??= running.HostPort;
                var key = string.IsNullOrEmpty(running.Digest) ? UnknownDigest : running.Digest;
                digests[key] = digests.TryGetValue(key, out var count) ? count + 1 : 1;
            }

            var errored = entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.LastError));
            if (errored?.LastError is { } message)
            {
                errorOn++;
                var at = errored.LastErrorAt ?? default;
                if (latestError is null || at > latestError.At)
                {
                    latestError = new FleetServiceError(instance.InstanceId, message, at);
                }
            }
        }

        return new FleetRollup(runningOn, instances.Count, digests, hostPort, errorOn, latestError);
    }
}

/// <summary>
/// The most recent reconcile error for a service across the fleet (tech-spec §4.5):
/// which instance reported it, the message, and when.
/// </summary>
/// <param name="InstanceId">The instance whose inventory carried the most recent error.</param>
/// <param name="Message">The recorded failure detail.</param>
/// <param name="At">When the failure was recorded.</param>
public sealed record FleetServiceError(string InstanceId, string Message, DateTimeOffset At);
