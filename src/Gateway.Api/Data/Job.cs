namespace Gateway.Api.Data;

/// <summary>
/// A scheduled job (docs/jobs.md section 2): a named script that runs on a cron
/// schedule, or on demand, as a short-lived container. Mutated only by the
/// Management API; the job scheduler reads it.
/// </summary>
public class Job
{
    /// <summary>Job name, <c>^[a-z0-9][a-z0-9-]{1,62}$</c>. Primary key.</summary>
    public string Name { get; set; } = default!;

    /// <summary>Five-field cron expression, or null for a manual-only job.</summary>
    public string? Schedule { get; set; }

    /// <summary>IANA time zone the schedule is evaluated in, e.g. America/Chicago.</summary>
    public string TimeZone { get; set; } = default!;

    /// <summary>The current script body, at most 256 KiB of UTF-8.</summary>
    public string Script { get; set; } = default!;

    /// <summary>Bumps each time the script body changes; starts at 1.</summary>
    public int ScriptVersion { get; set; }

    /// <summary>Runner image repository.</summary>
    public string Image { get; set; } = default!;

    /// <summary>Runner image tag.</summary>
    public string Tag { get; set; } = default!;

    /// <summary>Optional secrets-store reference (name or ARN) holding the job's env as flat JSON.</summary>
    public string? EnvSecretRef { get; set; }

    /// <summary>Run timeout in seconds, 30..21600.</summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>Whether the schedule is on. A disabled job can still be run manually.</summary>
    public bool Enabled { get; set; }

    /// <summary>Next scheduled fire time, or null when disabled or when there is no schedule.</summary>
    public DateTimeOffset? NextRunAt { get; set; }

    /// <summary>Cognito username or client id of the last mutator.</summary>
    public string UpdatedBy { get; set; } = default!;

    /// <summary>Timestamp of the last mutation.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
