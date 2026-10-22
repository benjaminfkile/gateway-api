namespace Gateway.Api.Data;

/// <summary>
/// One execution of a job (docs/jobs.md section 2). Not a foreign key to
/// <see cref="Job"/>: run history outlives a deleted job.
/// </summary>
public class JobRun
{
    /// <summary>Surrogate primary key (identity).</summary>
    public long Id { get; set; }

    /// <summary>The job name.</summary>
    public string Job { get; set; } = default!;

    /// <summary>'schedule' or 'manual' (see <c>JobRunTrigger</c>).</summary>
    public string Trigger { get; set; } = default!;

    /// <summary>Cognito username, client id, or 'schedule'.</summary>
    public string Actor { get; set; } = default!;

    /// <summary>Run status (see <c>JobRunStatus</c>).</summary>
    public string Status { get; set; } = default!;

    /// <summary>The instance that claimed the run, or null while queued.</summary>
    public string? InstanceId { get; set; }

    /// <summary>The image digest the run pulled, or null before the pull.</summary>
    public string? ImageDigest { get; set; }

    /// <summary>The job's script version when the run was queued.</summary>
    public int ScriptVersion { get; set; }

    /// <summary>Container exit code, or null when the container never exited normally.</summary>
    public int? ExitCode { get; set; }

    /// <summary>When the run was queued.</summary>
    public DateTimeOffset QueuedAt { get; set; }

    /// <summary>When the run started, or null while queued.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>When the run reached a terminal status, or null while active.</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>When an operator asked to cancel a running run, or null.</summary>
    public DateTimeOffset? CancelRequestedAt { get; set; }

    /// <summary>Last error or the last lines of output, at most 2 KiB.</summary>
    public string? Detail { get; set; }
}
