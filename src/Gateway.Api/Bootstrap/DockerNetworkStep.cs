using Microsoft.Extensions.Logging;

namespace Gateway.Api.Bootstrap;

/// <summary>
/// Bootstrap step (tech-spec §4.3): ensure the internal Docker network downstream
/// containers join exists. Inspects the network first and only creates it when it
/// is missing, so a second run is a no-op. Done via the <c>docker</c> CLI behind
/// <see cref="ILinuxHost"/> (rather than the Docker.DotNet reconciler runtime) so
/// bootstrap stays a pure host-provisioning concern with no daemon-library coupling.
/// </summary>
public sealed class DockerNetworkStep : IBootstrapStep
{
    private readonly ILinuxHost _host;
    private readonly BootstrapOptions _options;
    private readonly ILogger<DockerNetworkStep> _logger;

    public DockerNetworkStep(ILinuxHost host, BootstrapOptions options, ILogger<DockerNetworkStep> logger)
    {
        _host = host;
        _options = options;
        _logger = logger;
    }

    public string Name => "docker-network";

    public async Task<BootstrapStepResult> RunAsync(CancellationToken ct = default)
    {
        var network = _options.Network;

        var inspect = await _host.RunAsync("docker", new[] { "network", "inspect", network }, null, ct);
        if (inspect.Succeeded)
        {
            return BootstrapStepResult.Skipped($"Docker network '{network}' already exists.");
        }

        var create = await _host.RunAsync("docker", new[] { "network", "create", network }, null, ct);
        if (!create.Succeeded)
        {
            throw new InvalidOperationException(
                $"Failed to create Docker network '{network}': {create.StandardError}");
        }

        _logger.LogInformation("Created Docker network {Network}.", network);
        return BootstrapStepResult.Applied($"Created Docker network '{network}'.");
    }
}
