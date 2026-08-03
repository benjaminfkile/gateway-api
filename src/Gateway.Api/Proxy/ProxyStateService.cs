using Gateway.Api.Data;
using Gateway.Api.Manifest;
using Yarp.ReverseProxy.Configuration;

namespace Gateway.Api.Proxy;

/// <summary>
/// Translates the desired-state manifest into YARP routes and clusters and
/// hands them to <see cref="ManifestProxyConfigProvider"/>. One route + cluster
/// is built per <c>running</c> manifest entry:
/// <c>/{name}/{**catch-all}</c> → <c>http://{name}:{port}</c>, with the
/// <c>/{name}</c> prefix stripped so the downstream app receives the remainder
/// of the path and query unchanged and never has to know the gateway exists
/// (design invariant, tech-spec §1). Call <see cref="RefreshRoutesAsync"/> after
/// any manifest change to apply it without a restart.
/// </summary>
public sealed class ProxyStateService
{
    private readonly ManifestProxyConfigProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServiceAddressResolver _addressResolver;

    public ProxyStateService(
        ManifestProxyConfigProvider provider,
        IServiceScopeFactory scopeFactory,
        IServiceAddressResolver addressResolver)
    {
        _provider = provider;
        _scopeFactory = scopeFactory;
        _addressResolver = addressResolver;
    }

    /// <summary>Rebuild the proxy routes from the current manifest state.</summary>
    public async Task RefreshRoutesAsync(CancellationToken ct = default)
    {
        // The store may be scoped (EF), so resolve it inside a fresh scope.
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IManifestStore>();
        var manifests = await store.GetAllAsync(ct);

        var routes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();

        foreach (var manifest in manifests)
        {
            // Only 'running' services get a route; stopped ones are dropped from
            // rotation so requests to them 404 rather than hit a dead container.
            if (!string.Equals(manifest.DesiredStatus, "running", StringComparison.Ordinal))
            {
                continue;
            }

            var clusterId = $"cluster-{manifest.Name}";

            routes.Add(new RouteConfig
            {
                RouteId = $"route-{manifest.Name}",
                ClusterId = clusterId,
                Match = new RouteMatch
                {
                    Path = $"/{manifest.Name}/{{**catch-all}}",
                },
                // Strip the '/{name}' prefix; the app is gateway-unaware and must
                // see only its own paths. Query string is forwarded natively.
                Transforms = new[]
                {
                    new Dictionary<string, string> { ["PathRemovePrefix"] = $"/{manifest.Name}" },
                },
            });

            clusters.Add(new ClusterConfig
            {
                ClusterId = clusterId,
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["primary"] = new DestinationConfig
                    {
                        Address = _addressResolver.Resolve(manifest),
                    },
                },
            });
        }

        _provider.Update(routes, clusters);
    }
}
