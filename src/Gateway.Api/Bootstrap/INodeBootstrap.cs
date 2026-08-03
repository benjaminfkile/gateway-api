namespace Gateway.Api.Bootstrap;

/// <summary>
/// The node-bootstrap pipeline (tech-spec §4.3): runs the ordered
/// <see cref="IBootstrapStep"/> sequence once at startup to provision the box
/// (Docker daemon config, internal network, registry login, metrics-agent config).
/// This replaces the hand-rolled user-data bash of the old deployment shape (§2).
/// It runs only when <c>GATEWAY_BOOTSTRAP_ENABLED=true</c>.
/// </summary>
public interface INodeBootstrap
{
    /// <summary>
    /// Execute every step in order. A step that throws is logged and does not stop
    /// the remaining steps — bootstrap is best-effort convergence, re-run on the
    /// next boot (and, for registry login, on a timer).
    /// </summary>
    Task RunAsync(CancellationToken ct = default);
}
