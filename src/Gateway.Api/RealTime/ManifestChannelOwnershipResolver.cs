using Gateway.Api.Management;

namespace Gateway.Api.RealTime;

/// <summary>
/// <see cref="IChannelOwnershipResolver"/> over the manifest store (tech-spec §4.2,
/// task #593). Resolves ownership the same way the reconciler reads desired state —
/// from the manifest — but reads it through the shared <see cref="ManifestSnapshotCache"/>
/// (the one short-TTL, serve-stale, single-flight manifest read that <c>/hub</c> CORS
/// also projects off) so a burst of hub joins or publishes does not query the DB per
/// call, and a transient DB blip past the TTL does not 500 every non-ops join
/// (including PUBLIC channels — resolution runs before the auth-path null-check) and
/// every <c>/internal/publish</c>. The name→owner map is a <see cref="SnapshotProjection{T}"/>
/// rebuilt only when the snapshot changes.
/// <para>
/// Reserved / gateway-owned names (gateway, hub, internal, ops) are excluded from the
/// map, so their channels resolve to no owner and are neither joinable nor publishable —
/// keeping this projection consistent with the CORS projection, which already refused to
/// fold their origins in (review finding: the two projections previously disagreed about
/// a pre-reservation row named <c>hub</c>).
/// </para>
/// <para>
/// The 30s staleness is deliberate and safe: a newly-added service becomes joinable /
/// publishable within one TTL, and a rotated token keeps working for at most one TTL
/// while the store is reachable — or up to the snapshot cache's max-stale bound
/// (~5 min) during a store outage, after which resolution fails closed. Channel
/// ownership is coarse authorization, not a revocation surface.
/// </para>
/// </summary>
public sealed class ManifestChannelOwnershipResolver : IChannelOwnershipResolver
{
    private readonly SnapshotProjection<IReadOnlyDictionary<string, ChannelOwner>> _projection;

    public ManifestChannelOwnershipResolver(ManifestSnapshotCache snapshots)
    {
        _projection = new SnapshotProjection<IReadOnlyDictionary<string, ChannelOwner>>(snapshots, Project);
    }

    public async Task<ChannelOwner?> ResolveAsync(string prefix, CancellationToken ct = default)
    {
        var services = await _projection.GetAsync(ct);
        return services.TryGetValue(prefix, out var owner) ? owner : null;
    }

    private static IReadOnlyDictionary<string, ChannelOwner> Project(ManifestSnapshotCache.Snapshot snapshot)
    {
        var map = new Dictionary<string, ChannelOwner>(StringComparer.Ordinal);
        foreach (var m in snapshot.Services)
        {
            if (ManagementEndpoints.IsReservedName(m.Name))
            {
                continue;
            }

            map[m.Name] = new ChannelOwner(m.Name, m.RealtimePublishToken, m.RealtimeAuthPath, m.Port);
        }

        return map;
    }
}
