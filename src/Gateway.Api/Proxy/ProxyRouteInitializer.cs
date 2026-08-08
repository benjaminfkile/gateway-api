using Gateway.Api.Containers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Proxy;

/// <summary>
/// Builds the initial proxy route table from the manifest on host startup, so
/// the gateway is ready to route as soon as it starts serving. Subsequent
/// manifest changes are applied at runtime via
/// <see cref="ProxyStateService.RefreshRoutesAsync"/> — no restart needed.
/// <para>
/// Before building routes it primes the <see cref="ServiceHostPortMap"/> from the
/// running container inventory, so routes are built against the ports containers
/// are <b>actually</b> bound to. This is what keeps a blue-green candidate that was
/// promoted onto its Docker-assigned host port receiving traffic after a gateway
/// restart (tech-spec §7) — the in-memory swap that recorded that port is gone, so
/// the truth must come from the containers themselves.
/// </para>
/// </summary>
public sealed class ProxyRouteInitializer : IHostedService
{
    private readonly ProxyStateService _proxyState;
    private readonly ServiceHostPortMap _hostPorts;
    private readonly IContainerRuntime? _runtime;
    private readonly ILogger<ProxyRouteInitializer> _logger;

    public ProxyRouteInitializer(
        ProxyStateService proxyState,
        ServiceHostPortMap hostPorts,
        ILogger<ProxyRouteInitializer> logger,
        IContainerRuntime? runtime = null)
    {
        _proxyState = proxyState;
        _hostPorts = hostPorts;
        _logger = logger;
        _runtime = runtime;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await PrimeHostPortsAsync(cancellationToken);
        await _proxyState.RefreshRoutesAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task PrimeHostPortsAsync(CancellationToken ct)
    {
        if (_runtime is null)
        {
            return;
        }

        try
        {
            var containers = await _runtime.ListManagedContainersAsync(ct);
            _hostPorts.ReplaceFrom(containers);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No container runtime available (no Docker) or the daemon is not
            // reachable yet: fall back to manifest ports. The reconciler re-primes
            // the map on its first loop once Docker is up.
            _logger.LogDebug(ex, "Could not prime host-port map from container inventory at startup.");
        }
    }
}
