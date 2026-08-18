using System.Collections.Concurrent;
using Gateway.Api.Data;
using Gateway.Api.Manifest;
using Gateway.Api.Reconcile;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly ReconcilerOptions? _reconcilerOptions;
    private readonly ILogger<ProxyStateService> _logger;

    // Temporary per-service destination overrides used during a blue-green swap:
    // while set, a service's route points at the given address (the green
    // candidate) instead of its resolved canonical address. Cleared once the swap
    // completes and the candidate has been promoted to the canonical name.
    private readonly ConcurrentDictionary<string, string> _destinationOverrides =
        new(StringComparer.Ordinal);

    // Reconciler-mode only: services currently in the no-canonical-container state.
    // Used to log Warning+ ONCE per transition into that state (and Info once on
    // recovery / when the service leaves the desired-running set), so a service
    // stuck without a container port does not spam a warning every loop.
    // Guarded by _stateLock; only touched while rebuilding the route table.
    private readonly HashSet<string> _servicesWithoutContainer =
        new(StringComparer.Ordinal);
    private readonly object _stateLock = new();

    private static readonly IReadOnlySet<string> NoOverrides =
        new HashSet<string>(StringComparer.Ordinal);

    public ProxyStateService(
        ManifestProxyConfigProvider provider,
        IServiceScopeFactory scopeFactory,
        IServiceAddressResolver addressResolver,
        ServiceHostPortMap hostPorts,
        ReconcilerOptions? reconcilerOptions = null,
        ILogger<ProxyStateService>? logger = null)
    {
        _provider = provider;
        _scopeFactory = scopeFactory;
        _addressResolver = addressResolver;
        _hostPorts = hostPorts;
        _reconcilerOptions = reconcilerOptions;
        _logger = logger ?? NullLogger<ProxyStateService>.Instance;
    }

    /// <summary>
    /// The services currently pointed at a blue-green destination override (mid-swap).
    /// The reconciler uses this to skip those services when it reconciles the
    /// container-truth host-port map each loop, so an in-flight swap's override is
    /// never clobbered (tech-spec §7, requirement #2).
    /// </summary>
    public IReadOnlySet<string> ServicesWithDestinationOverride() =>
        _destinationOverrides.IsEmpty
            ? NoOverrides
            : new HashSet<string>(_destinationOverrides.Keys, StringComparer.Ordinal);

    /// <summary>Rebuild the proxy routes from the current manifest state.</summary>
    public async Task RefreshRoutesAsync(CancellationToken ct = default)
    {
        // The store may be scoped (EF), so resolve it inside a fresh scope.
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IManifestStore>();
        var manifests = await store.GetAllAsync(ct);

        var routes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();
        var runningNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var manifest in manifests)
        {
            // Only 'running' services get a route; stopped ones are dropped from
            // rotation so requests to them 404 rather than hit a dead container.
            if (!string.Equals(manifest.DesiredStatus, "running", StringComparison.Ordinal))
            {
                continue;
            }

            runningNames.Add(manifest.Name);

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

            var address = ResolveDestination(manifest);
            clusters.Add(new ClusterConfig
            {
                ClusterId = clusterId,
                // A null address is the reconciler-mode 'no canonical container'
                // signal: publish the cluster with NO destinations so YARP serves
                // 503 rather than silently proxying to a manifest-port host address
                // that could belong to a completely different service (incident
                // 2026-08-17). Proxy-only dev mode never returns null here — see
                // ResolveDestination — so the fallback stays exactly as before.
                Destinations = address is null
                    ? new Dictionary<string, DestinationConfig>(StringComparer.Ordinal)
                    : new Dictionary<string, DestinationConfig>
                    {
                        ["primary"] = new DestinationConfig { Address = address },
                    },
            });
        }

        // A service that dropped out of desired=running has no route this loop, so
        // it cannot be 'in the no-container state' any more — forget it silently so
        // the tracked set does not leak and a later re-add starts a fresh transition.
        ForgetUntrackedServices(runningNames);

        _provider.Update(routes, clusters);
    }

    /// <summary>
    /// The canonical destination address for a service, or null when reconciler
    /// mode has no canonical container port to forward to. Precedence:
    /// blue-green override → container-truth host port → (proxy-only) manifest port.
    /// The manifest-port fallback exists ONLY for proxy-only dev mode (no
    /// reconciler) where the developer runs the app locally on that port — see
    /// README, "Proxy-only dev mode". In reconciler mode the manifest port is a
    /// container-internal port; the corresponding HOST port is a Docker-assigned
    /// ephemeral that either has nothing listening or, worse, some unrelated
    /// container's port (incident 2026-08-17: a prod service silently proxied to
    /// its -dev sibling on the same container-side port). We return null so the
    /// route serves 503 and the health prober reports the service down.
    /// </summary>
    private string? ResolveDestination(ServiceManifest manifest)
    {
        if (_destinationOverrides.TryGetValue(manifest.Name, out var overrideAddress))
        {
            // Swap in progress: do NOT touch _servicesWithoutContainer — the swap
            // is orthogonal to whether a canonical container port exists, and its
            // transition should be reported when the canonical port itself moves.
            return overrideAddress;
        }

        if (_hostPorts.TryGet(manifest.Name, out var hostPort))
        {
            MarkContainerRecovered(manifest.Name);
            return _addressResolver.Resolve(manifest.Name, hostPort);
        }

        if (IsReconcilerEnabled)
        {
            MarkContainerMissing(manifest.Name);
            return null;
        }

        // Proxy-only dev mode: the manifest port is the host port the developer is
        // running the app on locally — the fallback is intentional and preserved.
        return _addressResolver.Resolve(manifest);
    }

    private bool IsReconcilerEnabled => _reconcilerOptions?.Enabled == true;

    private void MarkContainerMissing(string service)
    {
        bool becameMissing;
        lock (_stateLock)
        {
            becameMissing = _servicesWithoutContainer.Add(service);
        }

        if (becameMissing)
        {
            _logger.LogWarning(
                "Service '{Service}' has no canonical container host port recorded; "
                + "its route will serve 503 and it will report down in /api/health until "
                + "the reconciler brings the container back. The manifest port is NOT "
                + "used as a fallback in reconciler mode (would silently proxy to a "
                + "stranger; incident 2026-08-17).",
                service);
        }
    }

    private void MarkContainerRecovered(string service)
    {
        bool wasMissing;
        lock (_stateLock)
        {
            wasMissing = _servicesWithoutContainer.Remove(service);
        }

        if (wasMissing)
        {
            _logger.LogInformation(
                "Service '{Service}' canonical container host port is recorded again; "
                + "routing and health probing resumed.",
                service);
        }
    }

    private void ForgetUntrackedServices(IReadOnlySet<string> currentRunning)
    {
        lock (_stateLock)
        {
            if (_servicesWithoutContainer.Count == 0)
            {
                return;
            }

            _servicesWithoutContainer.RemoveWhere(name => !currentRunning.Contains(name));
        }
    }

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
