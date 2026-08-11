using Gateway.Api.Management;
using Gateway.Api.Manifest;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Api.RealTime;

/// <summary>
/// Backs the dynamic CORS policy on <c>/hub</c> (tech-spec §4.6, task #595) with the
/// set of browser origins allowed to negotiate a SignalR connection: the static
/// <c>GATEWAY_CORS_ORIGINS</c> ops-dashboard origins, plus the union of every manifest
/// service's <c>realtime_allowed_origins</c>.
/// <para>
/// The manifest half is read through <see cref="IManifestStore"/> and cached whole for
/// a short TTL (~30s) so a burst of <c>/hub/negotiate</c> preflights never runs a DB
/// query per request, yet a newly-upserted origin becomes effective within one TTL with
/// no gateway restart. A singleton (it outlives the scoped store), reaching the store
/// through an <see cref="IServiceScopeFactory"/> exactly as the reconciler and the
/// channel-ownership resolver do. The clock is injectable so the TTL is deterministic in
/// tests.
/// </para>
/// </summary>
public sealed class HubCorsOriginCache
{
    /// <summary>Default cache lifetime — roughly one reconcile loop.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlyList<string> _staticOrigins;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // The most recent union (static ∪ manifest) and when it was built. Replaced
    // wholesale on refresh so a reader always sees a consistent, complete set.
    private volatile Snapshot? _snapshot;

    public HubCorsOriginCache(
        IServiceScopeFactory scopeFactory,
        IEnumerable<string> staticOrigins,
        TimeSpan? ttl = null,
        TimeProvider? clock = null)
    {
        _scopeFactory = scopeFactory;
        // Normalize the static origins too so a dashboard origin compares byte-for-byte
        // against the browser Origin header just like the manifest ones.
        _staticOrigins = staticOrigins
            .SelectMany(o => RealtimeAllowedOrigins.Parse(o))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _ttl = ttl ?? DefaultTtl;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// The current set of allowed origins (static ∪ manifest), refreshing the manifest
    /// half when the cached snapshot has aged past the TTL. Case-insensitive membership
    /// so it matches the browser's lowercase <c>Origin</c> header.
    /// </summary>
    public async Task<IReadOnlySet<string>> GetAllowedOriginsAsync(CancellationToken ct = default)
    {
        var current = _snapshot;
        if (current is not null && _clock.GetUtcNow() - current.TakenAt < _ttl)
        {
            return current.Origins;
        }

        await _refreshLock.WaitAsync(ct);
        try
        {
            // Re-check: another caller may have refreshed while we waited on the lock.
            current = _snapshot;
            if (current is not null && _clock.GetUtcNow() - current.TakenAt < _ttl)
            {
                return current.Origins;
            }

            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IManifestStore>();
            var all = await store.GetAllAsync(ct);

            var set = new HashSet<string>(_staticOrigins, StringComparer.OrdinalIgnoreCase);
            foreach (var m in all)
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

            _snapshot = new Snapshot(set, _clock.GetUtcNow());
            return set;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private sealed record Snapshot(IReadOnlySet<string> Origins, DateTimeOffset TakenAt);
}
