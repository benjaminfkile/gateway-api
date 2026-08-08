using System.Collections.Concurrent;
using Gateway.Api.Data;
using Gateway.Api.Manifest;
using Yarp.ReverseProxy.Configuration;

namespace Gateway.Api.Proxy;

/// <summary>
/// Translates the desired-state manifest into YARP routes and clusters and
/// hands them to <see cref="ManifestProxyConfigProvider"/>. One route + cluster
/// is built per <c>running</c> manifest entry:
/// <c>/{name}/{**catch-all}</c> → <c>http://127.0.0.1:{port}</c>, with the
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
    private readonly ServiceHostPortMap _hostPorts;

    // Temporary per-service destination overrides used during a blue-green swap:
    // while set, a service's route points at the given address (the green
    // candidate) instead of its resolved canonical address. Cleared once the swap
    // completes and the candidate has been promoted to the canonical name.
    private readonly ConcurrentDictionary<string, string> _destinationOverrides =
        new(StringComparer.Ordinal);

    public ProxyStateService(
        ManifestProxyConfigProvider provider,
        IServiceScopeFactory scopeFactory,
        IServiceAddressResolver addressResolver,
        ServiceHostPortMap hostPorts)
    {
        _provider = provider;
        _scopeFactory = scopeFactory;
        _addressResolver = addressResolver;
        _hostPorts = hostPorts;
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
                        // A blue-green swap in progress points this route at the
                        // green candidate; otherwise forward to the port the
                        // container is actually bound to (a Docker-assigned host
                        // port), falling back to the manifest port.
                        Address = _destinationOverrides.TryGetValue(manifest.Name, out var overrideAddress)
                            ? overrideAddress
                            : ResolveCanonical(manifest),
                    },
                },
            });
        }

        _provider.Update(routes, clusters);
    }

    /// <summary>
    /// The canonical destination address for a service: the actual host port of its
    /// running container (container-truth) when known, else the manifest port. The
    /// container-truth port is what keeps a promoted-green container — bound to its
    /// Docker-assigned host port for the life of its process — reachable (tech-spec §7).
    /// </summary>
    private string ResolveCanonical(ServiceManifest manifest) =>
        _hostPorts.TryGet(manifest.Name, out var hostPort)
            ? _addressResolver.Resolve(manifest.Name, hostPort)
            : _addressResolver.Resolve(manifest);

    /// <summary>
    /// Point a service's route at <paramref name="address"/> (a blue-green
    /// candidate) and apply it immediately. In-flight requests to the previous
    /// destination drain via YARP's graceful destination removal (tech-spec §7).
    /// </summary>
    public Task SwapDestinationAsync(string serviceName, string address, CancellationToken ct = default)
    {
        _destinationOverrides[serviceName] = address;
        return RefreshRoutesAsync(ct);
    }

    /// <summary>
    /// Clear a blue-green destination override, returning the route to the
    /// service's canonical address (used after the candidate is promoted to the
    /// canonical container name). Idempotent.
    /// </summary>
    public Task ClearDestinationOverrideAsync(string serviceName, CancellationToken ct = default)
    {
        _destinationOverrides.TryRemove(serviceName, out _);
        return RefreshRoutesAsync(ct);
    }
}
