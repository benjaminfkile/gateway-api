namespace Gateway.Api.Data;

/// <summary>Well-known <c>load_run.status</c> values (docs/load.md section 3).</summary>
public static class LoadRunStatus
{
    public const string Queued = "queued";
    public const string FleetUp = "fleet_up";
    public const string GeneratorsUp = "generators_up";
    public const string Ramping = "ramping";
    public const string Holding = "holding";
    public const string Draining = "draining";
    public const string Succeeded = "succeeded";
    public const string Stopped = "stopped";
    public const string Expired = "expired";
    public const string Failed = "failed";
    public const string Lost = "lost";

    /// <summary>The statuses a run never leaves.</summary>
    public static readonly string[] Terminal = { Succeeded, Stopped, Expired, Failed, Lost };

    /// <summary>The statuses that hold the single load-run slot.</summary>
    public static readonly string[] NonTerminal = { Queued, FleetUp, GeneratorsUp, Ramping, Holding, Draining };

    /// <summary>True for <c>succeeded</c>, <c>stopped</c>, <c>expired</c>, <c>failed</c> and <c>lost</c>.</summary>
    public static bool IsTerminal(string status) => status is Succeeded or Stopped or Expired or Failed or Lost;

    /// <summary>
    /// True while an instance that already checked in gets its earlier assignment back
    /// on a repeated check-in: <c>generators_up</c>, <c>ramping</c> and <c>holding</c>.
    /// </summary>
    public static bool TakesRepeatCheckIn(string status) => status is GeneratorsUp or Ramping or Holding;
}

/// <summary>Well-known <c>load_run.stop_reason</c> values (docs/load.md section 2).</summary>
public static class LoadStopReason
{
    public const string Operator = "operator";
    public const string HardClock = "hard_clock";
    public const string Lost = "lost";
    public const string LeaderRestart = "leader_restart";
    public const string Failed = "failed";
}

/// <summary>Well-known <c>load_run_stat.source</c> values (docs/load.md section 2).</summary>
public static class LoadStatSource
{
    public const string Generator = "generator";
    public const string Fleet = "fleet";
}
