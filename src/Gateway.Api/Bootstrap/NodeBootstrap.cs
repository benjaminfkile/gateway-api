using Microsoft.Extensions.Logging;

namespace Gateway.Api.Bootstrap;

/// <summary>
/// Default <see cref="INodeBootstrap"/>: runs the registered <see cref="IBootstrapStep"/>
/// sequence in order, logging for each step whether it <i>changed</i> the box or was
/// <i>skipped</i> because the box was already converged (tech-spec §4.3). A step that
/// throws is logged and swallowed so a single failure never aborts the rest of the
/// pipeline — the box converges as far as it can and re-runs on the next boot.
/// </summary>
public sealed class NodeBootstrap : INodeBootstrap
{
    private readonly IEnumerable<IBootstrapStep> _steps;
    private readonly ILogger<NodeBootstrap> _logger;

    public NodeBootstrap(IEnumerable<IBootstrapStep> steps, ILogger<NodeBootstrap> logger)
    {
        _steps = steps;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        foreach (var step in _steps)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var result = await step.RunAsync(ct);
                if (result.Changed)
                {
                    _logger.LogInformation("Bootstrap step {Step} changed the box: {Detail}", step.Name, result.Detail);
                }
                else
                {
                    _logger.LogInformation("Bootstrap step {Step} skipped (already converged): {Detail}", step.Name, result.Detail);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Bootstrap step {Step} failed; continuing with remaining steps.", step.Name);
            }
        }
    }
}
