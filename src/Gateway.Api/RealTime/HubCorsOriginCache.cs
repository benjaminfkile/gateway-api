using Gateway.Api.Management;
using Microsoft.Extensions.DependencyInjection;

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
/// no gateway restart. The origin set is projected from the snapshot and memoized
/// against the snapshot reference so the union is recomputed only when the snapshot
/// actually changes, not on every preflight.
/// </para>
/// <para>
/// The static origins are always merged in, even while the manifest half is stale or a
/// refresh is failing: a transient DB blip must never 500 a preflight from a statically
/// configured ops origin that needed no DB in the first place.
/// </para>
/// </summary>
public sealed class HubCorsOriginCache
{
    /// <summary>Default cache lifetime — roughly one reconcile loop.</summary>
    /// <remarks>Retained for callers/tests that referenced it; the TTL now lives on
    /// <see cref="ManifestSnapshotCache"/>.</remarks>
    public static readonly TimeSpan DefaultTtl = ManifestSnapshotCache.DefaultTtl;

    private readonly ManifestSnapshotCache _snapshots;
    private readonly IReadOnlyList<string> _staticOrigins;
    private readonly object _projectionGate = new();

    // The origin union last projected, keyed off the snapshot it was projected from.
    private ManifestSnapshotCache.Snapshot? _projectedFrom;
    private IReadOnlySet<string>? _projected;

    public HubCorsOriginCache(ManifestSnapshotCache snapshots, IEnumerable<string> staticOrigins)
    {
        _snapshots = snapshots;
        // Normalize the static origins too so a dashboard origin compares byte-for-byte
        // against the browser Origin header just like the manifest ones.
        _staticOrigins = staticOrigins
            .SelectMany(o => RealtimeAllowedOrigins.Parse(o))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The current set of allowed origins (static ∪ manifest), refreshing the manifest
    /// half when the shared snapshot has aged past the TTL (serving the last good set if
    /// that refresh fails). Case-insensitive membership so it matches the browser's
    /// lowercase <c>Origin</c> header.
    /// </summary>
    public async Task<IReadOnlySet<string>> GetAllowedOriginsAsync(CancellationToken ct = default)
    {
        var snapshot = await _snapshots.GetAsync(ct);

        lock (_projectionGate)
        {
            if (ReferenceEquals(snapshot, _projectedFrom) && _projected is not null)
            {
                return _projected;
            }
        }

        var set = new HashSet<string>(_staticOrigins, StringComparer.OrdinalIgnoreCase);
        foreach (var m in snapshot.Services)
        {
            // A reserved / gateway-owned name (gateway, hub, internal, ops) must never
            // widen /hub CORS: its channels are gateway-owned (publishes 403, joins
            // demand the operator policy), so folding its origins in would grant
            // credentialed hub access for realtime that can never work. Skip it even if
            // a pre-reservation row still carries origins.
            if (ManagementEndpoints.IsReservedName(m.Name))
            {
                continue;
            }

            foreach (var origin in RealtimeAllowedOrigins.Parse(m.RealtimeAllowedOrigins))
            {
                set.Add(origin);
            }
        }

        lock (_projectionGate)
        {
            _projectedFrom = snapshot;
            _projected = set;
        }

        return set;
    }
}
