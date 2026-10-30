using Gateway.Api.Data;

namespace Gateway.Api.Management;

/// <summary>What a generator check-in came to (docs/load.md section 5).</summary>
public enum LoadCheckInOutcome
{
    /// <summary>The instance holds <see cref="LoadCheckInResult.Index"/> (new or repeated).</summary>
    Assigned,

    /// <summary>Every generator slot is taken.</summary>
    Full,

    /// <summary>The run is not in a status that takes check-ins.</summary>
    NotAccepting,

    /// <summary>No such run.</summary>
    NotFound,
}

/// <summary>
/// The answer of <see cref="ILoadStore.CheckInAsync"/>: the outcome, the assigned
/// zero-based index (meaningful only for <see cref="LoadCheckInOutcome.Assigned"/>)
/// and the run as it stands afterwards (null for <see cref="LoadCheckInOutcome.NotFound"/>).
/// </summary>
public sealed record LoadCheckInResult(LoadCheckInOutcome Outcome, int Index, LoadRun? Run);

/// <summary>
/// Read/write access to the load tables (docs/load.md section 2):
/// <c>load_scenario</c>, its <c>load_scenario_version</c> history, the
/// <c>load_run</c> ledger and the <c>load_run_stat</c> lines. The Management API
/// owns scenarios and inserts queued runs; the load scheduler claims and drives runs.
/// <para>
/// Two implementations exist: <see cref="EfLoadStore"/> over Postgres (production)
/// and <see cref="NullLoadStore"/> for a box with no database.
/// </para>
/// </summary>
public interface ILoadStore
{
    // ---------- scenarios ----------

    /// <summary>Every scenario, ordered by name.</summary>
    Task<IReadOnlyList<LoadScenario>> ListScenariosAsync(CancellationToken ct = default);

    /// <summary>One scenario by name, or null when absent.</summary>
    Task<LoadScenario?> GetScenarioAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Insert or update a scenario row. When <paramref name="version"/> is given the
    /// history row is inserted in the same save.
    /// </summary>
    Task UpsertScenarioAsync(LoadScenario scenario, LoadScenarioVersion? version = null, CancellationToken ct = default);

    /// <summary>Remove a scenario row. Its script history and runs stay.</summary>
    Task DeleteScenarioAsync(string name, CancellationToken ct = default);

    /// <summary>A scenario's script history, newest version first (kept after a delete).</summary>
    Task<IReadOnlyList<LoadScenarioVersion>> ListScenarioVersionsAsync(string scenario, CancellationToken ct = default);

    /// <summary>One script version, or null when absent.</summary>
    Task<LoadScenarioVersion?> GetScenarioVersionAsync(string scenario, int version, CancellationToken ct = default);

    // ---------- runs ----------

    /// <summary>
    /// Insert <paramref name="run"/> unless any run is non-terminal. Returns the
    /// inserted row with its generated id, or null when a run holds the slot (one
    /// load run at a time).
    /// </summary>
    Task<LoadRun?> TryCreateRunAsync(LoadRun run, CancellationToken ct = default);

    /// <summary>The newest non-terminal run, or null.</summary>
    Task<LoadRun?> GetActiveRunAsync(CancellationToken ct = default);

    /// <summary>The newest non-terminal run of <paramref name="scenario"/>, or null.</summary>
    Task<LoadRun?> GetActiveRunForScenarioAsync(string scenario, CancellationToken ct = default);

    /// <summary>One run by id, or null when absent.</summary>
    Task<LoadRun?> GetRunAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Runs newest first, at most <paramref name="limit"/>, only ids below
    /// <paramref name="before"/> when it is given (keyset paging).
    /// </summary>
    Task<IReadOnlyList<LoadRun>> ListRunsAsync(int limit, long? before, CancellationToken ct = default);

    /// <summary>
    /// Claim a queued run with one conditional update: <c>queued</c> to
    /// <c>generators_up</c>, <c>started_at</c> = <paramref name="now"/> and a timeline entry, only while the
    /// run is still queued and no other run is non-terminal. Returns the run after the
    /// claim, or null when the update matched nothing.
    /// </summary>
    Task<LoadRun?> ClaimQueuedRunAsync(long id, string by, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>
    /// The load scheduler's one conditional update per step (docs/load.md section 3).
    /// Writes <paramref name="next"/>'s <c>status</c>, <c>timeline</c>,
    /// <c>ramp_started_at</c>, <c>finished_at</c>, <c>stop_reason</c>, <c>detail</c> and
    /// <c>spec_token</c>, only while the stored row
    /// still has <paramref name="expected"/>'s status and timeline. A stop request and
    /// every other step append to the timeline, so a step decided on an older read
    /// matches nothing and two leaders ticking together cannot both act. Check-in
    /// columns are never written here. Returns the run after the update, or null when
    /// the update matched nothing.
    /// </summary>
    Task<LoadRun?> TryAdvanceRunAsync(LoadRun expected, LoadRun next, CancellationToken ct = default);

    /// <summary>
    /// Record <paramref name="runId"/> as the scenario's <c>last_validation_run_id</c>,
    /// only while the scenario's <c>script_version</c> is still
    /// <paramref name="scriptVersion"/> (a script changed during the run stays
    /// unvalidated). False when nothing matched.
    /// </summary>
    Task<bool> SetScenarioValidationAsync(string scenario, int scriptVersion, long runId, CancellationToken ct = default);

    /// <summary>
    /// Assign a generator index to <paramref name="instanceId"/> (docs/load.md section
    /// 5). A new instance gets the current <c>checked_in</c> as its index, and
    /// <c>checked_in</c> is incremented in the same conditional update (conditional on
    /// the status being <c>generators_up</c> and <c>checked_in</c> being unchanged),
    /// so two instances never share an index. An instance that already checked in gets
    /// its earlier index back while the run is in <c>generators_up</c>, <c>ramping</c>
    /// or <c>holding</c>.
    /// </summary>
    Task<LoadCheckInResult> CheckInAsync(long runId, string instanceId, CancellationToken ct = default);

    /// <summary>
    /// Stamp <c>stop_requested_at</c> on a non-terminal run (the first request wins)
    /// and add a timeline entry. Returns the run as it stands afterwards, or null when
    /// it does not exist; a terminal run comes back unchanged.
    /// </summary>
    Task<LoadRun?> RequestStopAsync(long id, string by, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Set or clear the verdict of a terminal run. False when the run is absent or not terminal.</summary>
    Task<bool> SetVerdictAsync(long id, string? verdict, CancellationToken ct = default);

    /// <summary>Remove a terminal run and its stats. False when the run is absent or not terminal.</summary>
    Task<bool> DeleteRunAsync(long id, CancellationToken ct = default);

    // ---------- stats ----------

    /// <summary>Insert one stats line; returns it with its generated id.</summary>
    Task<LoadRunStat> AppendStatAsync(LoadRunStat stat, CancellationToken ct = default);

    /// <summary>
    /// Raise every key of the run's <c>peaks</c> to the maximum of its stored value
    /// and <paramref name="sample"/>. A conditional update on the previous value, retried
    /// when a concurrent line won, so no maximum is lost.
    /// </summary>
    Task UpdatePeaksAsync(long runId, IReadOnlyDictionary<string, double> sample, CancellationToken ct = default);

    /// <summary>
    /// A run's stats lines ordered by <c>at</c> then id, filtered by the optional
    /// window (inclusive), source and generator index.
    /// </summary>
    Task<IReadOnlyList<LoadRunStat>> ListStatsAsync(
        long runId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? source,
        int? index,
        CancellationToken ct = default);

    /// <summary>The newest received generator line of each index of the run, ordered by index.</summary>
    Task<IReadOnlyList<LoadRunStat>> GetLatestGeneratorStatsAsync(long runId, CancellationToken ct = default);
}
