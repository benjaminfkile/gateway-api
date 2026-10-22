namespace Gateway.Api.Containers;

/// <summary>
/// Everything required to run one job-run container (docs/jobs.md section 3). Built
/// by the job scheduler from a job row, the run, and the resolved env. Unlike a
/// <see cref="ServiceContainerSpec"/> there is no port: a job container publishes and
/// exposes nothing, has no restart policy, and runs under a memory limit.
/// </summary>
/// <param name="Name">Container name, <c>job-{name}-{runId}</c>.</param>
/// <param name="Image">Registry image reference (repository).</param>
/// <param name="Digest">Resolved image digest; the container is created from <c>{image}@{digest}</c> when set.</param>
/// <param name="EnvVars">Environment variables, including resolved secrets and the <c>GATEWAY_JOB_*</c> set.</param>
/// <param name="Network">Internal Docker network the container joins.</param>
/// <param name="LogConfig">Log driver configuration (awslogs group <c>/gateway/jobs/{name}</c> in production).</param>
/// <param name="MemoryLimitBytes">Hard memory limit (<c>HostConfig.Memory</c>).</param>
/// <param name="Labels">
/// Labels stamped on the container: <see cref="ContainerLabels.Job"/> and
/// <see cref="ContainerLabels.JobRun"/>. Never <see cref="ContainerLabels.Managed"/>.
/// </param>
public sealed record JobContainerSpec(
    string Name,
    string Image,
    string? Digest,
    IReadOnlyDictionary<string, string> EnvVars,
    string Network,
    LogDriverConfig LogConfig,
    long MemoryLimitBytes,
    IReadOnlyDictionary<string, string> Labels);
