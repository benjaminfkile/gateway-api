using Gateway.Api.Data;

namespace Gateway.Api.Management;

/// <summary>
/// No-op <see cref="IJobStore"/> used when no database is configured (local dev
/// without Postgres), so the job endpoints still bind. Writes are dropped and reads
/// return empty; the Management API is unreachable without a Cognito authority
/// anyway, the same as <see cref="NullDeployStore"/>.
/// </summary>
public sealed class NullJobStore : IJobStore
{
    public Task<IReadOnlyList<Job>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Job>>(Array.Empty<Job>());

    public Task<Job?> GetAsync(string name, CancellationToken ct = default) =>
        Task.FromResult<Job?>(null);

    public Task UpsertAsync(Job job, JobScriptVersion? scriptVersion = null, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeleteAsync(string name, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<JobScriptVersion>> ListScriptVersionsAsync(string job, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<JobScriptVersion>>(Array.Empty<JobScriptVersion>());

    public Task<JobScriptVersion?> GetScriptVersionAsync(string job, int version, CancellationToken ct = default) =>
        Task.FromResult<JobScriptVersion?>(null);

    public Task<JobRun?> TryEnqueueRunAsync(JobRun run, CancellationToken ct = default) =>
        Task.FromResult<JobRun?>(run);

    public Task<JobRun?> GetActiveRunAsync(string job, CancellationToken ct = default) =>
        Task.FromResult<JobRun?>(null);

    public Task<IReadOnlyDictionary<string, JobRun>> GetLastRunsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, JobRun>>(new Dictionary<string, JobRun>());

    public Task<JobRun?> GetRunAsync(long id, CancellationToken ct = default) =>
        Task.FromResult<JobRun?>(null);

    public Task<IReadOnlyList<JobRun>> ListRunsAsync(string job, int limit, long? before, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<JobRun>>(Array.Empty<JobRun>());

    public Task<JobRun?> CancelRunAsync(long id, DateTimeOffset now, CancellationToken ct = default) =>
        Task.FromResult<JobRun?>(null);

    public Task<IReadOnlyList<Job>> ListDueAsync(DateTimeOffset now, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Job>>(Array.Empty<Job>());

    public Task<JobRun?> TryClaimScheduledRunAsync(
        string job, DateTimeOffset dueAt, DateTimeOffset? nextRunAt, JobRun run, CancellationToken ct = default) =>
        Task.FromResult<JobRun?>(null);

    public Task<IReadOnlyList<JobRun>> ListRunsByStatusAsync(string status, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<JobRun>>(Array.Empty<JobRun>());

    public Task<bool> TryClaimQueuedRunAsync(long id, string instanceId, DateTimeOffset now, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task SetRunImageDigestAsync(long id, string digest, CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> FinishRunAsync(
        long id,
        string instanceId,
        string status,
        int? exitCode,
        string? detail,
        DateTimeOffset finishedAt,
        CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<bool> MarkRunLostAsync(
        long id, string instanceId, DateTimeOffset now, string detail, CancellationToken ct = default) =>
        Task.FromResult(false);
}
