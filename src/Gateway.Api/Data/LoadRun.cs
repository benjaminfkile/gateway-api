namespace Gateway.Api.Data;

/// <summary>
/// One load run (docs/load.md section 2). Not a foreign key to
/// <see cref="LoadScenario"/>: run history outlives a deleted scenario.
/// </summary>
public class LoadRun
{
    /// <summary>Surrogate primary key (identity).</summary>
    public long Id { get; set; }

    /// <summary>The scenario name.</summary>
    public string Scenario { get; set; } = default!;

    /// <summary>The scenario's script version when the run was started.</summary>
    public int ScenarioVersion { get; set; }

    /// <summary>Manifest service name the scenario is aimed at.</summary>
    public string Target { get; set; } = default!;

    /// <summary>True for the one-generator, one-user validation run.</summary>
    public bool Validation { get; set; }

    /// <summary>Cognito username or client id that started the run.</summary>
    public string Actor { get; set; } = default!;

    /// <summary>Run status (see <see cref="LoadRunStatus"/>).</summary>
    public string Status { get; set; } = default!;

    /// <summary>Generator count, 1..<c>Load:MaxGenerators</c>.</summary>
    public int Generators { get; set; }

    /// <summary>Virtual users each generator carries, 1..the scenario's maximum.</summary>
    public int VusPerGenerator { get; set; }

    /// <summary>Ramp in seconds, 0..7200.</summary>
    public int RampSeconds { get; set; }

    /// <summary>Hold in seconds, 0..10800.</summary>
    public int HoldSeconds { get; set; }

    /// <summary>JSON object of strings: the scenario's defaults merged with the run's overrides.</summary>
    public string Params { get; set; } = "{}";

    /// <summary>The absolute clock the run cannot outlive.</summary>
    public DateTimeOffset HardStopAt { get; set; }

    /// <summary>Lowercase hex sha256 of the run token the generators present.</summary>
    public string TokenHash { get; set; } = default!;

    /// <summary>
    /// The run token itself, held only from Start until the leader has written it into
    /// the generator spec secret (docs/load.md section 5); null afterwards. Never part
    /// of a response or a log line.
    /// </summary>
    public string? SpecToken { get; set; }

    /// <summary>Generators that have checked in.</summary>
    public int CheckedIn { get; set; }

    /// <summary>JSON object of instance id to assigned generator index, <c>{}</c> at first.</summary>
    public string Checkins { get; set; } = "{}";

    /// <summary>When the run was queued.</summary>
    public DateTimeOffset QueuedAt { get; set; }

    /// <summary>When the status left <c>queued</c>, or null.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>When the ramp started, or null.</summary>
    public DateTimeOffset? RampStartedAt { get; set; }

    /// <summary>When the run reached a terminal status, or null.</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>When an operator asked to stop the run, or null.</summary>
    public DateTimeOffset? StopRequestedAt { get; set; }

    /// <summary>Why the run drained (see <see cref="LoadStopReason"/>), or null.</summary>
    public string? StopReason { get; set; }

    /// <summary>One line the operator writes afterwards, at most 500 chars.</summary>
    public string? Verdict { get; set; }

    /// <summary>Last error or note, at most 2 KiB.</summary>
    public string? Detail { get; set; }

    /// <summary>JSON object: the maximum of every numeric stat seen, keyed <c>metric.field</c>.</summary>
    public string Peaks { get; set; } = "{}";

    /// <summary>JSON array of <c>{ at, status, by, note }</c>.</summary>
    public string Timeline { get; set; } = "[]";

    /// <summary>A shallow copy: every column is a value or an immutable string.</summary>
    public LoadRun Clone() => (LoadRun)MemberwiseClone();
}
