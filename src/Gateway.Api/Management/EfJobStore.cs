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
}
