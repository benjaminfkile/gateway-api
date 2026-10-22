namespace Gateway.Api.Containers;

/// <summary>
/// A job-run container found by label through
/// <see cref="IContainerRuntime.FindJobContainerAsync"/>, so a restarted gateway can
/// resume waiting on a run it started before the restart (docs/jobs.md section 3).
/// </summary>
/// <param name="Id">Docker container id.</param>
/// <param name="Name">Container name, <c>job-{name}-{runId}</c>.</param>
/// <param name="Job">The <see cref="ContainerLabels.Job"/> label value.</param>
/// <param name="RunId">The <see cref="ContainerLabels.JobRun"/> label value.</param>
/// <param name="State">Docker container state, e.g. <c>running</c> or <c>exited</c>.</param>
/// <param name="StartedAt">When the container started, or null when unknown.</param>
public sealed record JobContainerInfo(
    string Id,
    string Name,
    string Job,
    long RunId,
    string State,
    DateTimeOffset? StartedAt);
