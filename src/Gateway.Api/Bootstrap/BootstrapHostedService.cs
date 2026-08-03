using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Bootstrap;

/// <summary>
/// Runs the node-bootstrap pipeline once at startup (tech-spec §4.3), gated on
/// <c>GATEWAY_BOOTSTRAP_ENABLED=true</c> (<see cref="BootstrapOptions.Enabled"/>).
/// Disabled by default so the build/test environment — which has no root
/// filesystem to mutate, no Docker daemon, and no AWS — is never touched. Runs in a
/// background task so it does not block host startup / request serving.
/// <para>
/// The pipeline (and thus its registry/ECR-backed step) is resolved lazily, only
/// after the enable gate, so a disabled or region-less box never constructs an AWS
/// client at startup.
/// </para>
/// </summary>
public sealed class BootstrapHostedService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly BootstrapOptions _options;
    private readonly ILogger<BootstrapHostedService> _logger;

    public BootstrapHostedService(IServiceProvider services, BootstrapOptions options, ILogger<BootstrapHostedService> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation(
                "Node bootstrap disabled ({EnvVar} != true); not provisioning the box.",
                BootstrapOptions.EnabledEnvVar);
            return;
        }

        _logger.LogInformation("Node bootstrap enabled; running provisioning steps once.");
        try
        {
            var bootstrap = _services.GetRequiredService<INodeBootstrap>();
            await bootstrap.RunAsync(stoppingToken);
            _logger.LogInformation("Node bootstrap complete.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down mid-bootstrap; nothing to do.
        }
    }
}
