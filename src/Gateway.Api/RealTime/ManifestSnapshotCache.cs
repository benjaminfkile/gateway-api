using Gateway.Api.Data;
using Gateway.Api.Manifest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.RealTime;

/// <summary>
/// The single short-TTL snapshot of the whole manifest shared by every realtime
/// consumer that would otherwise read the manifest per request (tech-spec §4.2). Both
/// the <c>/hub</c> CORS origin set (<see cref="HubCorsOriginCache"/>) and channel
/// ownership (<see cref="ManifestChannelOwnershipResolver"/>) project off this one
/// cache, so a burst of hub negotiates, joins, and publishes triggers at most one
/// <see cref="IManifestStore.GetAllAsync"/> per TTL for the whole instance — not two
/// full-manifest reads per surface.
/// <para>
/// Resilience is the point of collapsing the two former copies into one: a refresh runs
/// inside a try/catch and, on failure, <b>serves the last good (expired) snapshot</b>
/// rather than propagating — stale is strictly better than a 500 on both the CORS
/// preflight and the join/publish hot paths, where a transient DB blip past the TTL
/// would otherwise fail every request carrying an Origin header or every non-ops join.
/// A failed refresh arms a short retry backoff so a hard-down DB is probed once per
/// backoff, not once per request, and concurrent waiters coalesce onto a single
/// in-flight refresh (single-flight) instead of each re-running the query serially
/// behind the lock.
/// </para>
/// A singleton (it outlives the scoped store), reaching the store through an
/// <see cref="IServiceScopeFactory"/> exactly as the reconciler does. The clock is
/// injectable so the TTL and backoff are deterministic in tests.
/// </summary>
public sealed class ManifestSnapshotCache
{
    /// <summary>Default cache lifetime — roughly one reconcile loop.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(30);

    /// <summary>How long a failed refresh suppresses further store probes.</summary>
    public static readonly TimeSpan DefaultRetryBackoff = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _retryBackoff;
    private readonly TimeProvider _clock;
    private readonly ILogger<ManifestSnapshotCache>? _logger;
    private readonly object _gate = new();

    // Never null: seeded with an empty, permanently-expired snapshot so the first read always
    // refreshes and, if that first refresh fails, callers still get an (empty) snapshot
    // to project the static half against rather than a null-ref.
    private Snapshot _snapshot = Snapshot.Empty;

    // The one refresh in progress, if any — concurrent callers await this instead of
    // each launching (and serializing behind the lock on) their own store query.
    private Task<Snapshot>? _inFlight;

    // Earliest time a fresh store probe is allowed again after a failed refresh.
    private DateTimeOffset _nextRefreshAt = DateTimeOffset.MinValue;

    public ManifestSnapshotCache(
        IServiceScopeFactory scopeFactory,
        TimeSpan? ttl = null,
        TimeProvider? clock = null,
        ILogger<ManifestSnapshotCache>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _ttl = ttl ?? DefaultTtl;
        _retryBackoff = DefaultRetryBackoff;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>
    /// The current manifest snapshot, refreshing it when the cached one has aged past
    /// the TTL. Returns the snapshot object by reference so a consumer can memoize its
    /// own projection (origin set, ownership map) against the reference and re-project
    /// only when the snapshot actually changes. On a refresh failure the last good
    /// (expired) snapshot is returned — never an exception.
    /// </summary>
    public async Task<Snapshot> GetAsync(CancellationToken ct = default)
    {
        Task<Snapshot> refresh;
        lock (_gate)
        {
            var current = _snapshot;
            var now = _clock.GetUtcNow();

            // Fresh within the TTL — the fast path taken by the vast majority of reads.
            if (now - current.TakenAt < _ttl)
            {
                return current;
            }

            if (_inFlight is not null)
            {
                // Single-flight: a refresh is already running; await that one result.
                refresh = _inFlight;
            }
            else if (now < _nextRefreshAt)
            {
                // Inside the retry backoff after a failed refresh: serve the stale
                // snapshot without touching the store, so a hard-down DB is probed
                // once per backoff rather than once per request.
                return current;
            }
            else
            {
                refresh = _inFlight = RefreshAsync();
            }
        }

        // Await outside the lock. A caller's own cancellation only abandons its wait —
        // the shared refresh (started with CancellationToken.None) runs to completion so
        // the other waiters still get their result.
        return await refresh.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<Snapshot> RefreshAsync()
    {
        // Yield first so this method never runs its body (or its completion bookkeeping)
        // synchronously under the caller's lock, even when the store completes
        // synchronously — that would re-enter the lock and clobber the _inFlight handle
        // the caller is about to assign.
        await Task.Yield();

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IManifestStore>();
            var all = await store.GetAllAsync(CancellationToken.None).ConfigureAwait(false);

            var fresh = new Snapshot(all, _clock.GetUtcNow());
            lock (_gate)
            {
                _snapshot = fresh;
                _nextRefreshAt = DateTimeOffset.MinValue;
                _inFlight = null;
            }

            return fresh;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _inFlight = null;
                _nextRefreshAt = _clock.GetUtcNow() + _retryBackoff;
                var stale = _snapshot;
                _logger?.LogWarning(
                    ex,
                    "Manifest snapshot refresh failed; serving {Count} cached service(s) (stale) "
                    + "until the next probe after {Backoff}.",
                    stale.Services.Count,
                    _retryBackoff);
                return stale;
            }
        }
    }

    /// <summary>
    /// An immutable snapshot of every manifest row plus the instant it was read.
    /// Handed out by reference so consumers can key their projections off identity.
    /// </summary>
    public sealed record Snapshot(IReadOnlyList<ServiceManifest> Services, DateTimeOffset TakenAt)
    {
        /// <summary>The permanently-expired empty seed used before the first successful refresh.</summary>
        public static readonly Snapshot Empty = new(Array.Empty<ServiceManifest>(), DateTimeOffset.MinValue);
    }
}
