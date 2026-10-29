namespace Gateway.Api.Data;

/// <summary>
/// A load scenario (docs/load.md section 2): a k6 script plus its parameters,
/// registered with the gateway and owned by the repository of the application it
/// tests. Mutated only by the Management API.
/// </summary>
public class LoadScenario
{
    /// <summary>Scenario name, <c>^[a-z0-9][a-z0-9-]{1,62}$</c>. Primary key.</summary>
    public string Name { get; set; } = default!;

    /// <summary><c>owner/name</c> of the repository that owns the script.</summary>
    public string Repository { get; set; } = default!;

    /// <summary>The commit the script was pushed from, or null when unknown.</summary>
    public string? CommitSha { get; set; }

    /// <summary>The current k6 script body, at most 256 KiB of UTF-8.</summary>
    public string Script { get; set; } = default!;

    /// <summary>Bumps each time the script body changes; starts at 1.</summary>
    public int ScriptVersion { get; set; }

    /// <summary>Runner image repository.</summary>
    public string Image { get; set; } = default!;

    /// <summary>Runner image tag.</summary>
    public string Tag { get; set; } = default!;

    /// <summary>JSON object of strings, <c>{}</c> when none.</summary>
    public string ParamsDefault { get; set; } = "{}";

    /// <summary>The most virtual users one generator may carry, 1..100000.</summary>
    public int MaxVusPerGenerator { get; set; }

    /// <summary>The newest validation run that succeeded, or null (cleared when the script changes).</summary>
    public long? LastValidationRunId { get; set; }

    /// <summary>Cognito username or client id of the last mutator.</summary>
    public string UpdatedBy { get; set; } = default!;

    /// <summary>Timestamp of the last mutation.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
