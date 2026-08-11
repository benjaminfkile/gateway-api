using Gateway.Api.Management;

namespace Gateway.Api.RealTime;

/// <summary>
/// Backs the dynamic CORS policy on <c>/hub</c> (tech-spec §4.6, task #595) with the
/// set of browser origins allowed to negotiate a SignalR connection: the static
/// <c>GATEWAY_CORS_ORIGINS</c> ops-dashboard origins, plus the union of every manifest
/// service's <c>realtime_allowed_origins</c>.
/// <para>
/// The manifest half comes from the shared <see cref="ManifestSnapshotCache"/> — the one
/// short-TTL, serve-stale, single-flight read of the manifest that channel ownership
/// also projects off — so a burst of <c>/hub/negotiate</c> preflights never runs a DB
/// query per request, yet a newly-upserted origin becomes effective within one TTL with
/// no gateway restart. The origin union is a <see cref="SnapshotProjection{T}"/> rebuilt
/// only when the snapshot actually changes, not on every preflight.
/// </para>
/// <para>
/// The static origins are always merged in, even while the manifest half is stale or a
/// refresh is failing: a transient DB blip must never 500 a preflight from a statically
/// configured ops origin that needed no DB in the first place.
/// </para>
/// </summary>
public sealed class HubCorsOriginCache
{
    private readonly SnapshotProjection<IReadOnlySet<string>> _projection;
    private readonly IReadOnlyList<string> _staticOrigins;

    public HubCorsOriginCache(ManifestSnapshotCache snapshots, IEnumerable<string> staticOrigins)
    {
        // Normalize the static origins too so a dashboard origin compares byte-for-byte
        // against the browser Origin header just like the manifest ones.
        _staticOrigins = staticOrigins
            .SelectMany(o => RealtimeAllowedOrigins.Parse(o))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _projection = new SnapshotProjection<IReadOnlySet<string>>(snapshots, Project);
    }

    /// <summary>
    /// The current set of allowed origins (static ∪ manifest), refreshing the manifest
    /// half when the shared snapshot has aged past the TTL (serving the last good set if
    /// that refresh fails, within the cache's max-stale bound). Case-insensitive
    /// membership so it matches the browser's lowercase <c>Origin</c> header.
    /// </summary>
    public Task<IReadOnlySet<string>> GetAllowedOriginsAsync(CancellationToken ct = default) =>
        _projection.GetAsync(ct);

    private IReadOnlySet<string> Project(ManifestSnapshotCache.Snapshot snapshot)
    {
        var set = new HashSet<string>(_staticOrigins, StringComparer.OrdinalIgnoreCase);
        foreach (var m in snapshot.Services)
        {
            // A reserved / gateway-owned name (gateway, hub, internal, ops) must never
            // widen /hub CORS: its channels are gateway-owned (they resolve to no channel
            // owner — see ManifestChannelOwnershipResolver — so realtime for them can
            // never work). Skip it even if a pre-reservation row still carries origins.
            if (ManagementEndpoints.IsReservedName(m.Name))
            {
                continue;
            }

            foreach (var origin in RealtimeAllowedOrigins.Parse(m.RealtimeAllowedOrigins))
            {
                set.Add(origin);
            }
        }

        return set;
    }
}
