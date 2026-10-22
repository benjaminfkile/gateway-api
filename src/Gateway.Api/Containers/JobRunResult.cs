namespace Gateway.Api.Containers;

/// <summary>
/// How a job-run container ended (docs/jobs.md section 3), as reported by
/// <see cref="IContainerRuntime.RunJobContainerAsync"/> and
/// <see cref="IContainerRuntime.WaitJobContainerAsync"/>.
/// </summary>
/// <param name="ExitCode">The container's exit code, or null when it was killed or never reported one.</param>
/// <param name="TimedOut">True when the timeout elapsed and the container was killed.</param>
/// <param name="Cancelled">True when the cancellation token fired and the container was killed.</param>
/// <param name="StartedAt">When the container started (or when waiting began, on re-attach without a start time).</param>
/// <param name="FinishedAt">When the container exited or was killed.</param>
/// <param name="Tail">The last 20 lines of the container's output, read through the container logs API.</param>
public sealed record JobRunResult(
    int? ExitCode,
    bool TimedOut,
    bool Cancelled,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string Tail)
{
    /// <summary>Number of output lines kept in <see cref="Tail"/>.</summary>
    public const int TailLines = 20;
}
