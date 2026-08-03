namespace Gateway.Api.Bootstrap;

/// <summary>
/// One idempotent step of the node-bootstrap pipeline (tech-spec §4.3): configure
/// the Docker daemon, ensure the internal network, log in to the registry, write
/// the metrics-agent config. Each step compares desired state against what is
/// already on the box and only acts on a difference, so running the pipeline a
/// second time is a no-op. Every step reports whether it changed anything via
/// <see cref="BootstrapStepResult"/> so the pipeline can log <i>changed vs. skipped</i>.
/// </summary>
public interface IBootstrapStep
{
    /// <summary>Stable, human-readable step name for logging (e.g. <c>docker-daemon-config</c>).</summary>
    string Name { get; }

    /// <summary>
    /// Bring the box to the step's desired state. Must be idempotent: a second run
    /// against an already-converged box performs no mutation and returns
    /// <see cref="BootstrapStepResult.Changed"/> = <c>false</c>.
    /// </summary>
    Task<BootstrapStepResult> RunAsync(CancellationToken ct = default);
}

/// <summary>Outcome of a single bootstrap step: whether it changed the box, and why.</summary>
public sealed record BootstrapStepResult(bool Changed, string Detail)
{
    /// <summary>The step found the box already converged and did nothing.</summary>
    public static BootstrapStepResult Skipped(string detail) => new(false, detail);

    /// <summary>The step mutated the box to converge it.</summary>
    public static BootstrapStepResult Applied(string detail) => new(true, detail);
}
