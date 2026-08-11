using Gateway.Api.Data;
using Gateway.Api.Manifest;
using Gateway.Api.RealTime;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Api.Tests;

/// <summary>
/// Both realtime surfaces that read the manifest — the /hub CORS origin set and channel
/// ownership — must keep serving the last good snapshot when the store goes down past the
/// TTL (task #607). A transient DB blip must not 500 a preflight, and — the case the
/// review called out — must not fail a PUBLIC channel join, whose ownership resolution
/// runs before the auth-path null-check. These exercise the two consumers directly over a
/// store that throws after priming, on a controllable clock so the TTL is deterministic.
/// </summary>
public class RealtimeStaleServeTests
{
    // A store that serves a fixed set until Throw is flipped, then simulates an outage.
    private sealed class ToggleStore : IManifestStore
    {
        public bool Throw;
        public List<ServiceManifest> Services { get; } = new();

        public Task<IReadOnlyList<ServiceManifest>> GetAllAsync(CancellationToken ct = default)
        {
            if (Throw)
            {
                throw new InvalidOperationException("simulated store outage");
            }

            IReadOnlyList<ServiceManifest> all = Services.ToList();
            return Task.FromResult(all);
        }

        public Task<ServiceManifest?> GetAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertAsync(ServiceManifest manifest, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetDesiredStatusAsync(string name, string desiredStatus, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static (ManifestSnapshotCache Cache, ToggleStore Store, ManualTimeProvider Clock) Build()
    {
        var store = new ToggleStore();
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

    private static ServiceManifest Service(string name, string? origins = null, string? authPath = null) => new()
    {
        Name = name,
        Image = $"registry/{name}",
        Tag = "latest",
        Port = 8080,
        DesiredStatus = "running",
        RealtimeAllowedOrigins = origins,
        RealtimeAuthPath = authPath,
        UpdatedBy = "seed",
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task HubCors_ServesStaticAndCachedOrigins_WhenStoreDownPastTtl()
    {
        var (cache, store, clock) = Build();
        store.Services.Add(Service("svc-a", "https://chat.example.com"));
        var corsCache = new HubCorsOriginCache(cache, new[] { "https://ops.example.com" });

        var primed = await corsCache.GetAllowedOriginsAsync();
        Assert.Contains("https://chat.example.com", primed);
        Assert.Contains("https://ops.example.com", primed);

        // Store goes down and the TTL lapses: the preflight set must still carry both the
        // static ops origin (which never needed the DB) and the last-known service origin.
        store.Throw = true;
        clock.Now += ManifestSnapshotCache.DefaultTtl + TimeSpan.FromSeconds(1);

        var stale = await corsCache.GetAllowedOriginsAsync();
        Assert.Contains("https://ops.example.com", stale);
        Assert.Contains("https://chat.example.com", stale);
    }

    [Fact]
    public async Task Ownership_PublicJoinResolves_WhenStoreDownPastTtl()
    {
        var (cache, store, clock) = Build();
        store.Services.Add(Service("svc-a")); // no auth path => public channel
        var resolver = new ManifestChannelOwnershipResolver(cache);

        var primed = await resolver.ResolveAsync("svc-a");
        Assert.NotNull(primed);
        Assert.Null(primed!.AuthPath);

        // Store down, TTL lapsed: resolution runs before the hub's auth-path null-check,
        // so the public join must still find its owner from the cached snapshot.
        store.Throw = true;
        clock.Now += ManifestSnapshotCache.DefaultTtl + TimeSpan.FromSeconds(1);

        var stale = await resolver.ResolveAsync("svc-a");
        Assert.NotNull(stale);
        Assert.Equal("svc-a", stale!.Service);
        Assert.Null(stale.AuthPath);
    }
}
