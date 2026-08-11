namespace Gateway.Api.RealTime;

/// <summary>
/// <see cref="IChannelOwnershipResolver"/> over the manifest store (tech-spec §4.2,
/// task #593). Resolves ownership the same way the reconciler reads desired state —
/// from the manifest — but reads it through the shared <see cref="ManifestSnapshotCache"/>
/// (the one short-TTL, serve-stale, single-flight manifest read that <c>/hub</c> CORS
/// also projects off) so a burst of hub joins or publishes does not query the DB per
/// call, and a transient DB blip past the TTL does not 500 every non-ops join
/// (including PUBLIC channels — resolution runs before the auth-path null-check) and
/// every <c>/internal/publish</c>. The name→owner map is projected from the snapshot and
/// memoized against the snapshot reference so it is rebuilt only when the snapshot
/// changes, not on every call.
/// <para>
/// The 30s staleness is deliberate and safe: a newly-added service becomes joinable /
/// publishable within one TTL, and a rotated token keeps working for at most one TTL —
/// the same order as a reconcile loop. Channel ownership is coarse authorization, not
/// a revocation surface.
/// </para>
/// </summary>
public sealed class ManifestChannelOwnershipResolver : IChannelOwnershipResolver
{
    /// <summary>Default cache lifetime — roughly one reconcile loop.</summary>
    /// <remarks>Retained for callers/tests that referenced it; the TTL now lives on
    /// <see cref="ManifestSnapshotCache"/>.</remarks>
    public static readonly TimeSpan DefaultTtl = ManifestSnapshotCache.DefaultTtl;

    private readonly ManifestSnapshotCache _snapshots;
    private readonly object _projectionGate = new();

    // The ownership map last projected, keyed off the snapshot it was projected from.
    private ManifestSnapshotCache.Snapshot? _projectedFrom;
    private IReadOnlyDictionary<string, ChannelOwner>? _projected;

    public ManifestChannelOwnershipResolver(ManifestSnapshotCache snapshots)
    {
        _snapshots = snapshots;
    }

    public async Task<ChannelOwner?> ResolveAsync(string prefix, CancellationToken ct = default)
    {
        var services = await GetServicesAsync(ct);
        return services.TryGetValue(prefix, out var owner) ? owner : null;
    }

    private async Task<IReadOnlyDictionary<string, ChannelOwner>> GetServicesAsync(CancellationToken ct)
    {
        var snapshot = await _snapshots.GetAsync(ct);

        lock (_projectionGate)
        {
            if (ReferenceEquals(snapshot, _projectedFrom) && _projected is not null)
            {
                return _projected;
            }
        }

        var map = new Dictionary<string, ChannelOwner>(StringComparer.Ordinal);
        foreach (var m in snapshot.Services)
        {
            map[m.Name] = new ChannelOwner(m.Name, m.RealtimePublishToken, m.RealtimeAuthPath, m.Port);
        }

        lock (_projectionGate)
        {
            _projectedFrom = snapshot;
            _projected = map;
        }

        return map;
    }
}
