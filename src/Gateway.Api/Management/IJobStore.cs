using Gateway.Api.Data;

namespace Gateway.Api.Management;

/// <summary>
/// Read/write access to the scheduled-job tables (docs/jobs.md section 2):
/// <c>job</c>, its <c>job_script_version</c> history, and the <c>job_run</c> ledger.
/// The Management API owns job rows and enqueues manual runs; the job scheduler
/// claims and drives runs.
/// <para>
/// Two implementations exist: <see cref="EfJobStore"/> over Postgres (production)
/// and <see cref="NullJobStore"/> for a box with no database (local dev / tests use a
/// Sqlite-backed <see cref="EfJobStore"/> or a fake).
/// </para>
/// </summary>
public interface IJobStore
{
    /// <summary>Every job, ordered by name.</summary>
    Task<IReadOnlyList<Job>> ListAsync(CancellationToken ct = default);

    /// <summary>One job by name, or null when absent.</summary>
    Task<Job?> GetAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Insert or update a job row. When <paramref name="scriptVersion"/> is given the
    /// history row is inserted in the same save, so a version bump and its history
    /// land together.
    /// </summary>
    Task UpsertAsync(Job job, JobScriptVersion? scriptVersion = null, CancellationToken ct = default);

    /// <summary>
    /// Remove a job and its script history. Run rows stay: history outlives the job.
    /// </summary>
    Task DeleteAsync(string name, CancellationToken ct = default);

    /// <summary>A job's script history, newest version first.</summary>
    Task<IReadOnlyList<JobScriptVersion>> ListScriptVersionsAsync(string job, CancellationToken ct = default);

    /// <summary>One script version, or null when absent.</summary>
    Task<JobScriptVersion?> GetScriptVersionAsync(string job, int version, CancellationToken ct = default);

    /// <summary>
    /// Insert <paramref name="run"/> unless the job already has a run in
    /// <c>queued</c> or <c>running</c>. Returns the inserted row with its generated
    /// id, or null when an active run exists (one run at a time per job).
    /// </summary>
    Task<JobRun?> TryEnqueueRunAsync(JobRun run, CancellationToken ct = default);

    /// <summary>The job's run in <c>queued</c> or <c>running</c>, or null.</summary>
    Task<JobRun?> GetActiveRunAsync(string job, CancellationToken ct = default);

    /// <summary>The most recent run of each job that has one, keyed by job name.</summary>
    Task<IReadOnlyDictionary<string, JobRun>> GetLastRunsAsync(CancellationToken ct = default);

    /// <summary>One run by id, or null when absent.</summary>
    Task<JobRun?> GetRunAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// A job's runs newest first, at most <paramref name="limit"/>, only ids below
    /// <paramref name="before"/> when it is given (keyset paging).
    /// </summary>
    Task<IReadOnlyList<JobRun>> ListRunsAsync(string job, int limit, long? before, CancellationToken ct = default);

    /// <summary>
    /// Request cancellation of a run. A <c>queued</c> run is finalised as
    /// <c>cancelled</c> at once; a <c>running</c> run only gets
    /// <c>cancel_requested_at</c> stamped (its instance kills the container). Both are
    /// conditional updates so they cannot race a claim. Returns the run as it stands
    /// afterwards, or null when it does not exist; a run already finished comes back
    /// unchanged.
    /// </summary>
    Task<JobRun?> CancelRunAsync(long id, DateTimeOffset now, CancellationToken ct = default);
}
