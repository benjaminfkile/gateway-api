using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Bootstrap;

/// <summary>
/// Keeps the Docker daemon's registry login fresh (tech-spec §4.3: registry login
/// "refreshed periodically"). The initial login is performed by the bootstrap
/// pipeline; this service re-runs the <see cref="RegistryLoginStep"/> every
/// <see cref="BootstrapOptions.RegistryRefreshInterval"/> (6h by default) so the
/// short-lived registry token (ECR: ~12h) never expires under the reconciler.
/// Gated on <c>GATEWAY_BOOTSTRAP_ENABLED=true</c>, so it is inert in build/test.
/// The step is idempotent, so a refresh with unchanged credentials is a no-op.
/// <para>
/// The login step (and its registry/ECR-backed dependency) is resolved lazily, only
/// after the enable gate, so a disabled or region-less box never constructs an AWS
/// client at startup.
/// </para>
/// </summary>
public sealed class RegistryCredentialRefreshService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly BootstrapOptions _options;
    private readonly ILogger<RegistryCredentialRefreshService> _logger;

    public RegistryCredentialRefreshService(
        IServiceProvider services,
        BootstrapOptions options,
        ILogger<RegistryCredentialRefreshService> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var step = _services.GetRequiredService<RegistryLoginStep>();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.RegistryRefreshInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var result = await step.RunAsync(stoppingToken);
                _logger.LogInformation(
                    "Registry credential refresh: {State} — {Detail}",
                    result.Changed ? "renewed" : "unchanged",
                    result.Detail);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A refresh failure must not crash the loop; the next tick retries.
                _logger.LogError(ex, "Registry credential refresh failed");
            }
        }
    }
}
