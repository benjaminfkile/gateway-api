using Gateway.Api.Data;
using Gateway.Api.Manifest;
using Gateway.Api.RealTime;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Api.Tests;

/// <summary>
/// The shared manifest-snapshot cache (task #607) is the one short-TTL manifest read that
/// both /hub CORS and channel ownership project off. These cover the three resilience
/// properties the review demanded of it: SERVE-STALE on a refresh failure (a transient DB
/// blip must never propagate to a preflight/join/publish), SINGLE-FLIGHT (N concurrent
/// misses coalesce onto one store call, not N serial queries behind the lock), and a
/// retry BACKOFF (a hard-down store is probed once per backoff, not once per request).
/// </summary>
public class ManifestSnapshotCacheTests
{
    // A manifest store whose GetAllAsync the test drives explicitly: it counts calls,
    // can be told to throw (simulate a DB outage), and — for the single-flight test —
    // can block until released so concurrent callers are guaranteed to overlap.
    private sealed class ControllableStore : IManifestStore
    {
        private readonly object _gate = new();
        private TaskCompletionSource? _block;
        public int Calls;
        public bool Throw;
        public List<ServiceManifest> Services { get; } = new();

        public void BlockUntilReleased() => _block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _block?.TrySetResult();

        public async Task<IReadOnlyList<ServiceManifest>> GetAllAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            var block = _block;
            if (block is not null)
            {
                await block.Task;
            }

            if (Throw)
            {
                throw new InvalidOperationException("simulated store outage");
            }

            lock (_gate)
            {
                return Services.Select(Clone).ToList();
            }
        }

        private static ServiceManifest Clone(ServiceManifest m) => new()
        {
            Name = m.Name,
            Image = m.Image,
            Tag = m.Tag,
            Port = m.Port,
            DesiredStatus = m.DesiredStatus,
            RealtimePublishToken = m.RealtimePublishToken,
            RealtimeAuthPath = m.RealtimeAuthPath,
            RealtimeAllowedOrigins = m.RealtimeAllowedOrigins,
            UpdatedBy = m.UpdatedBy,
            UpdatedAt = m.UpdatedAt,
        };

        public Task<ServiceManifest?> GetAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertAsync(ServiceManifest manifest, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetDesiredStatusAsync(string name, string desiredStatus, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static (ManifestSnapshotCache Cache, ControllableStore Store, ManualTimeProvider Clock) Build()
    {
        var store = new ControllableStore();
        var services = new ServiceCollection();
        services.AddSingleton<IManifestStore>(store);
        var provider = services.BuildServiceProvider();
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UnixEpoch };
        var cache = new ManifestSnapshotCache(
            provider.GetRequiredService<IServiceScopeFactory>(),
            ManifestSnapshotCache.DefaultTtl,
            clock);
        return (cache, store, clock);
    }

    private static ServiceManifest Service(string name) => new()
    {
        Name = name,
        Image = $"registry/{name}",
        Tag = "latest",
        Port = 8080,
        DesiredStatus = "running",
        UpdatedBy = "seed",
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task RefreshFailure_ServesLastGoodSnapshot()
    {
        var (cache, store, clock) = Build();
        store.Services.Add(Service("svc-a"));

        // Prime a good snapshot.
        var primed = await cache.GetAsync();
        Assert.Equal("svc-a", Assert.Single(primed.Services).Name);

        // The store goes down and the TTL lapses: the next read must serve the stale
        // snapshot rather than throwing.
        store.Throw = true;
        clock.Now += ManifestSnapshotCache.DefaultTtl + TimeSpan.FromSeconds(1);

        var stale = await cache.GetAsync();
        Assert.Equal("svc-a", Assert.Single(stale.Services).Name);
    }

    [Fact]
    public async Task ConcurrentMisses_CoalesceToOneStoreCall()
    {
        var (cache, store, _) = Build();
        store.Services.Add(Service("svc-a"));
        store.BlockUntilReleased();

        // Ten concurrent first-reads all miss; single-flight must funnel them through one
        // in-flight refresh. The blocked store keeps that refresh open until every caller
        // has had the chance to coalesce onto it.
        var reads = Enumerable.Range(0, 10).Select(_ => cache.GetAsync()).ToArray();
        store.Release();
        var results = await Task.WhenAll(reads);

        Assert.Equal(1, store.Calls);
        Assert.All(results, r => Assert.Equal("svc-a", Assert.Single(r.Services).Name));
    }

    [Fact]
    public async Task FailedRefresh_ProbesStoreOncePerBackoff_NotPerRequest()
    {
        var (cache, store, clock) = Build();
        store.Services.Add(Service("svc-a"));
        await cache.GetAsync();

        // Store down + TTL lapsed: the first read past the TTL probes once and fails.
        store.Throw = true;
        clock.Now += ManifestSnapshotCache.DefaultTtl + TimeSpan.FromSeconds(1);
        await cache.GetAsync();
        Assert.Equal(2, store.Calls); // 1 prime + 1 failing probe

        // Further reads inside the backoff window must NOT re-probe the down store.
        await cache.GetAsync();
        await cache.GetAsync();
        Assert.Equal(2, store.Calls);

        // Once the backoff elapses, exactly one more probe is allowed.
        clock.Now += ManifestSnapshotCache.DefaultRetryBackoff + TimeSpan.FromSeconds(1);
        await cache.GetAsync();
        Assert.Equal(3, store.Calls);
    }
}
