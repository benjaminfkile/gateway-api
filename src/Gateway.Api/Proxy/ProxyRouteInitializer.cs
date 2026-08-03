using Microsoft.Extensions.Hosting;

namespace Gateway.Api.Proxy;

/// <summary>
/// Builds the initial proxy route table from the manifest on host startup, so
/// the gateway is ready to route as soon as it starts serving. Subsequent
/// manifest changes are applied at runtime via
/// <see cref="ProxyStateService.RefreshRoutesAsync"/> — no restart needed.
/// </summary>
public sealed class ProxyRouteInitializer : IHostedService
{
    private readonly ProxyStateService _proxyState;

    public ProxyRouteInitializer(ProxyStateService proxyState)
    {
        _proxyState = proxyState;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _proxyState.RefreshRoutesAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
