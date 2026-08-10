using Gateway.Api.Manifest;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Api.RealTime;

/// <summary>
/// <see cref="IChannelOwnershipResolver"/> over the manifest store (tech-spec §4.2,
/// task #593). Resolves ownership the same way the reconciler reads desired state —
/// through <see cref="IManifestStore"/> — but caches a whole-manifest snapshot for a
/// short TTL (~30s) so a burst of hub joins or publishes does not query the DB per
/// call. A singleton (it outlives the scoped store), so it reaches the store through
/// an <see cref="IServiceScopeFactory"/> exactly as the reconciler does.
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
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeSpan _ttl;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // Snapshot of service name -> publish token, and when it was taken. Replaced
    // wholesale on refresh so readers always see a consistent map.
    private volatile Snapshot? _snapshot;

    public ManifestChannelOwnershipResolver(IServiceScopeFactory scopeFactory, TimeSpan? ttl = null)
    {
        _scopeFactory = scopeFactory;
        _ttl = ttl ?? DefaultTtl;
    }

    public async Task<ChannelOwner?> ResolveAsync(string prefix, CancellationToken ct = default)
    {
        var services = await GetServicesAsync(ct);
        return services.TryGetValue(prefix, out var token)
            ? new ChannelOwner(prefix, token)
            : null;
    }

    private async Task<IReadOnlyDictionary<string, string?>> GetServicesAsync(CancellationToken ct)
    {
        var current = _snapshot;
        if (current is not null && DateTimeOffset.UtcNow - current.TakenAt < _ttl)
        {
            return current.Services;
        }

        await _refreshLock.WaitAsync(ct);
        try
        {
            // Re-check: another caller may have refreshed while we waited on the lock.
            current = _snapshot;
            if (current is not null && DateTimeOffset.UtcNow - current.TakenAt < _ttl)
            {
                return current.Services;
            }

            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IManifestStore>();
            var all = await store.GetAllAsync(ct);

            var map = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var m in all)
            {
                map[m.Name] = m.RealtimePublishToken;
            }

            _snapshot = new Snapshot(map, DateTimeOffset.UtcNow);
            return map;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private sealed record Snapshot(IReadOnlyDictionary<string, string?> Services, DateTimeOffset TakenAt);
}
