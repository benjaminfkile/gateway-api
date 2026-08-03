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
public sealed record FleetRollup(
    int RunningOn,
    int TotalInstances,
    IReadOnlyDictionary<string, int> Digests)
{
    /// <summary>Placeholder key for an instance running the service on an unknown digest.</summary>
    public const string UnknownDigest = "unknown";

    /// <summary>
    /// Compute the rollup for <paramref name="service"/> across the given fleet rows.
    /// An instance counts toward <see cref="RunningOn"/> (and its digest bucket) when
    /// its inventory lists the service in the <c>running</c> state.
    /// </summary>
    public static FleetRollup Compute(string service, IReadOnlyCollection<InstanceStatus> instances)
    {
        var digests = new Dictionary<string, int>(StringComparer.Ordinal);
        var runningOn = 0;

        foreach (var instance in instances)
        {
            var entry = InstanceServicesJson.Parse(instance.Services)
                .FirstOrDefault(e => string.Equals(e.Name, service, StringComparison.Ordinal)
                    && string.Equals(e.State, "running", StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                continue;
            }

            runningOn++;
            var key = string.IsNullOrEmpty(entry.Digest) ? UnknownDigest : entry.Digest;
            digests[key] = digests.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        return new FleetRollup(runningOn, instances.Count, digests);
    }
}
