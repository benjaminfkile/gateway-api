using Gateway.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Management;

/// <summary>
/// <see cref="IJobStore"/> over <see cref="GatewayDbContext"/>: the production
/// implementation reading/writing the Postgres <c>job</c>,
/// <c>job_script_version</c> and <c>job_run</c> tables (docs/jobs.md section 2).
/// Registered scoped, alongside the scoped DbContext.
/// </summary>
public sealed class EfJobStore : IJobStore
{
    private readonly GatewayDbContext _db;

    public EfJobStore(GatewayDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<Job>> ListAsync(CancellationToken ct = default)
    {
        return await _db.Jobs.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
    }

    public async Task<Job?> GetAsync(string name, CancellationToken ct = default)
    {
        return await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Name == name, ct);
    }

    public async Task UpsertAsync(Job job, JobScriptVersion? scriptVersion = null, CancellationToken ct = default)
    {
        var existing = await _db.Jobs.FirstOrDefaultAsync(x => x.Name == job.Name, ct);
        if (existing is null)
        {
            _db.Jobs.Add(job);
        }
        else
        {
            _db.Entry(existing).CurrentValues.SetValues(job);
        }

        if (scriptVersion is not null)
        {
            _db.JobScriptVersions.Add(scriptVersion);
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        await _db.JobScriptVersions.Where(x => x.Job == name).ExecuteDeleteAsync(ct);
        await _db.Jobs.Where(x => x.Name == name).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<JobScriptVersion>> ListScriptVersionsAsync(string job, CancellationToken ct = default)
    {
        return await _db.JobScriptVersions.AsNoTracking()
            .Where(x => x.Job == job)
            .OrderByDescending(x => x.Version)
            .ToListAsync(ct);
    }

    public async Task<JobScriptVersion?> GetScriptVersionAsync(string job, int version, CancellationToken ct = default)
    {
        return await _db.JobScriptVersions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Job == job && x.Version == version, ct);
    }

    public async Task<JobRun?> TryEnqueueRunAsync(JobRun run, CancellationToken ct = default)
    {
        if (await GetActiveRunAsync(run.Job, ct) is not null)
        {
            return null;
        }

        _db.JobRuns.Add(run);
        try
        {
            await _db.SaveChangesAsync(ct);
            return run;
        }
        catch (DbUpdateException)
        {
            // Lost the race to a concurrent enqueue or claim: the partial unique index
            // ux_job_run_active admits one queued-or-running run per job.
            _db.Entry(run).State = EntityState.Detached;
            return null;
        }
    }

    public async Task<JobRun?> GetActiveRunAsync(string job, CancellationToken ct = default)
    {
        return await _db.JobRuns.AsNoTracking()
            .Where(x => x.Job == job && (x.Status == JobRunStatus.Queued || x.Status == JobRunStatus.Running))
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, JobRun>> GetLastRunsAsync(CancellationToken ct = default)
    {
        var lastIds = _db.JobRuns.GroupBy(x => x.Job).Select(g => g.Max(x => x.Id));
        var runs = await _db.JobRuns.AsNoTracking()
            .Where(x => lastIds.Contains(x.Id))
            .ToListAsync(ct);
        return runs.ToDictionary(x => x.Job, StringComparer.Ordinal);
    }

    public async Task<JobRun?> GetRunAsync(long id, CancellationToken ct = default)
    {
        return await _db.JobRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<IReadOnlyList<JobRun>> ListRunsAsync(string job, int limit, long? before, CancellationToken ct = default)
    {
        var query = _db.JobRuns.AsNoTracking().Where(x => x.Job == job);
        if (before is { } cursor)
        {
            query = query.Where(x => x.Id < cursor);
        }

        return await query.OrderByDescending(x => x.Id).Take(limit).ToListAsync(ct);
    }

    public async Task<JobRun?> CancelRunAsync(long id, DateTimeOffset now, CancellationToken ct = default)
    {
        // A queued run never started, so it is finalised here. Conditional on the
        // status so it cannot overwrite a run a scheduler claimed in the meantime.
        var cancelled = await _db.JobRuns
            .Where(x => x.Id == id && x.Status == JobRunStatus.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, JobRunStatus.Cancelled)
                .SetProperty(x => x.CancelRequestedAt, now)
                .SetProperty(x => x.FinishedAt, now), ct);

        if (cancelled == 0)
        {
            // A running run is killed by its instance; only the request is recorded.
            // The first request wins so the stamp reflects when cancel was asked for.
            await _db.JobRuns
                .Where(x => x.Id == id && x.Status == JobRunStatus.Running && x.CancelRequestedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.CancelRequestedAt, now), ct);
        }

        return await GetRunAsync(id, ct);
    }

    public async Task<IReadOnlyList<Job>> ListDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        // The job table is small, so the time comparison runs in memory; that keeps
        // it portable (Sqlite in tests cannot translate a DateTimeOffset comparison).
        var scheduled = await _db.Jobs.AsNoTracking()
            .Where(x => x.Enabled && x.NextRunAt != null)
            .ToListAsync(ct);
        return scheduled
            .Where(x => x.NextRunAt <= now)
            .OrderBy(x => x.NextRunAt)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<JobRun?> TryClaimScheduledRunAsync(
        string job, DateTimeOffset dueAt, DateTimeOffset? nextRunAt, JobRun run, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // The conditional advance is the claim: a second scheduler that read the same
        // due time updates nothing (on Postgres it waits for the first commit, then
        // re-checks the predicate and finds next_run_at moved).
        var advanced = await _db.Jobs
            .Where(x => x.Name == job && x.Enabled && x.NextRunAt == dueAt)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextRunAt, nextRunAt), ct);
        if (advanced == 0)
        {
            return null;
        }

        if (await GetActiveRunAsync(job, ct) is not null)
        {
            // One run at a time: leave next_run_at where it was so the job runs once
            // when the active run ends.
            return null;
        }

        _db.JobRuns.Add(run);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            _db.Entry(run).State = EntityState.Detached;
            return null;
        }

        await tx.CommitAsync(ct);
        return run;
    }

    public async Task<IReadOnlyList<JobRun>> ListRunsByStatusAsync(string status, CancellationToken ct = default)
    {
        return await _db.JobRuns.AsNoTracking()
            .Where(x => x.Status == status)
            .OrderBy(x => x.Id)
            .ToListAsync(ct);
    }

    public async Task<bool> TryClaimQueuedRunAsync(long id, string instanceId, DateTimeOffset now, CancellationToken ct = default)
    {
        var claimed = await _db.JobRuns
            .Where(x => x.Id == id && x.Status == JobRunStatus.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, JobRunStatus.Running)
                .SetProperty(x => x.InstanceId, instanceId)
                .SetProperty(x => x.StartedAt, now), ct);
        return claimed == 1;
    }

    public async Task SetRunImageDigestAsync(long id, string digest, CancellationToken ct = default)
    {
        await _db.JobRuns
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ImageDigest, digest), ct);
    }

    public async Task<bool> FinishRunAsync(
        long id,
        string instanceId,
        string status,
        int? exitCode,
        string? detail,
        DateTimeOffset finishedAt,
        CancellationToken ct = default)
    {
        var finished = await _db.JobRuns
            .Where(x => x.Id == id
                && x.InstanceId == instanceId
                && (x.Status == JobRunStatus.Running || x.Status == JobRunStatus.Lost))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.ExitCode, exitCode)
                .SetProperty(x => x.Detail, detail)
                .SetProperty(x => x.FinishedAt, finishedAt), ct);
        return finished == 1;
    }

    public async Task<bool> MarkRunLostAsync(
        long id, string instanceId, DateTimeOffset now, string detail, CancellationToken ct = default)
    {
        var lost = await _db.JobRuns
            .Where(x => x.Id == id && x.InstanceId == instanceId && x.Status == JobRunStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, JobRunStatus.Lost)
                .SetProperty(x => x.Detail, detail)
                .SetProperty(x => x.FinishedAt, now), ct);
        return lost == 1;
    }
}
