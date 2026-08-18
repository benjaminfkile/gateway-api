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

    // When each service's destination override was first set — used to BOUND how long
    // the reconciler will skip its container-truth host-port reconciliation for that
    // service (task #99, incident 2026-08-17). Without a bound, a service stuck in
    // perpetual blue-green (e.g. an unsatisfiable pinned digest looping forever) is
    // essentially always in swap, so its map entry can freeze at a stale port that
    // Docker later reassigns to a sibling — the prod→dev misrouting incident. The stamp
    // is set on the transition into swap and cleared on the transition out, so a
    // continuous swap keeps its original start time.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _swapStartedAt =
        new(StringComparer.Ordinal);

    // Injected for tests so they can advance time without waiting real seconds.
    private readonly TimeProvider _timeProvider;

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
        ILogger<ProxyStateService>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _provider = provider;
        _scopeFactory = scopeFactory;
        _addressResolver = addressResolver;
        _hostPorts = hostPorts;
        _reconcilerOptions = reconcilerOptions;
        _logger = logger ?? NullLogger<ProxyStateService>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
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

    /// <summary>
    /// The services currently pointed at a blue-green destination override whose swap
    /// has been in progress for less than <paramref name="maxAge"/> — the reconciler's
    /// bounded-skip set for the container-truth host-port map (task #99, incident
    /// 2026-08-17). A service whose swap has been running longer than the bound is
    /// deliberately EXCLUDED so container-truth reconciliation resumes and its route
    /// follows the real canonical container's port; a perpetual swap can never freeze
    /// the map at a port that Docker has since reassigned to a sibling.
    /// </summary>
    public IReadOnlySet<string> ServicesWithDestinationOverride(TimeSpan maxAge)
    {
        if (_destinationOverrides.IsEmpty)
        {
            return NoOverrides;
        }

        var now = _timeProvider.GetUtcNow();
        var fresh = new HashSet<string>(StringComparer.Ordinal);
        foreach (var service in _destinationOverrides.Keys)
        {
            // A swap with no recorded start-time is treated as freshly started so the
            // pre-bound behavior is preserved (defensive: the pair is set atomically
            // below, but a race between reader and writer is possible).
            var startedAt = _swapStartedAt.TryGetValue(service, out var at) ? at : now;
            if (now - startedAt <= maxAge)
            {
                fresh.Add(service);
            }
        }

        return fresh;
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
    /// <para>
    /// Also stamps when this swap started (once, on the transition into swap) so a
    /// perpetually-swapping service does not keep its skip renewed forever — the
    /// reconciler drops it out of the bounded-skip set once the age exceeds its bound
    /// (task #99, incident 2026-08-17).
    /// </para>
    /// </summary>
    public Task SwapDestinationAsync(string serviceName, string address, CancellationToken ct = default)
    {
        // TryAdd so a re-swap on an already-swapping service keeps its ORIGINAL start
        // stamp; the elapsed-since-swap-began measurement stays continuous, not per
        // SwapDestinationAsync call.
        _swapStartedAt.TryAdd(serviceName, _timeProvider.GetUtcNow());
        _destinationOverrides[serviceName] = address;
        return RefreshRoutesAsync(ct);
    }

    /// <summary>
    /// Clear a blue-green destination override, returning the route to the
    /// service's canonical address (used after the candidate is promoted to the
    /// canonical container name). Idempotent. Also clears the swap-start stamp so a
    /// future swap on this service is measured from its own start (task #99).
    /// </summary>
    public Task ClearDestinationOverrideAsync(string serviceName, CancellationToken ct = default)
    {
        _destinationOverrides.TryRemove(serviceName, out _);
        _swapStartedAt.TryRemove(serviceName, out _);
        return RefreshRoutesAsync(ct);
    }
}
