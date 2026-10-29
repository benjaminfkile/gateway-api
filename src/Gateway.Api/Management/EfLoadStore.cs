using Gateway.Api.Data;
using Gateway.Api.Load;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Management;

/// <summary>
/// <see cref="ILoadStore"/> over <see cref="GatewayDbContext"/>: the production
/// implementation reading/writing the Postgres <c>load_scenario</c>,
/// <c>load_scenario_version</c>, <c>load_run</c> and <c>load_run_stat</c> tables
/// (docs/load.md section 2). Registered scoped, alongside the scoped DbContext.
/// <para>
/// Time comparisons and time ordering run in memory after the run-scoped query, the
/// same as <see cref="EfJobStore.ListDueAsync"/>, so the store stays portable (Sqlite
/// in tests cannot translate a <see cref="DateTimeOffset"/> comparison); on Postgres the
/// stats window is also applied in SQL so the (run_id, at) index does the work.
/// </para>
/// </summary>
public sealed class EfLoadStore : ILoadStore
{
    // Bound on the compare-and-swap retries of check-in and peaks. Contention is the
    // generator count (Load:MaxGenerators), far below this.
    private const int MaxAttempts = 64;

    private readonly GatewayDbContext _db;

    public EfLoadStore(GatewayDbContext db)
    {
        _db = db;
    }

    // ---------- scenarios ----------

    public async Task<IReadOnlyList<LoadScenario>> ListScenariosAsync(CancellationToken ct = default)
    {
        return await _db.LoadScenarios.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
    }

    public async Task<LoadScenario?> GetScenarioAsync(string name, CancellationToken ct = default)
    {
        return await _db.LoadScenarios.AsNoTracking().FirstOrDefaultAsync(x => x.Name == name, ct);
    }

    public async Task UpsertScenarioAsync(LoadScenario scenario, LoadScenarioVersion? version = null, CancellationToken ct = default)
    {
        var existing = await _db.LoadScenarios.FirstOrDefaultAsync(x => x.Name == scenario.Name, ct);
        if (existing is null)
        {
            _db.LoadScenarios.Add(scenario);
        }
        else
        {
            _db.Entry(existing).CurrentValues.SetValues(scenario);
        }

        if (version is not null)
        {
            _db.LoadScenarioVersions.Add(version);
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteScenarioAsync(string name, CancellationToken ct = default)
    {
        await _db.LoadScenarios.Where(x => x.Name == name).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<LoadScenarioVersion>> ListScenarioVersionsAsync(string scenario, CancellationToken ct = default)
    {
        return await _db.LoadScenarioVersions.AsNoTracking()
            .Where(x => x.Scenario == scenario)
            .OrderByDescending(x => x.Version)
            .ToListAsync(ct);
    }

    public async Task<LoadScenarioVersion?> GetScenarioVersionAsync(string scenario, int version, CancellationToken ct = default)
    {
        return await _db.LoadScenarioVersions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Scenario == scenario && x.Version == version, ct);
    }

    // ---------- runs ----------

    public async Task<LoadRun?> TryCreateRunAsync(LoadRun run, CancellationToken ct = default)
    {
        if (await GetActiveRunAsync(ct) is not null)
        {
            return null;
        }

        _db.LoadRuns.Add(run);
        try
        {
            await _db.SaveChangesAsync(ct);
            return run;
        }
        catch (DbUpdateException)
        {
            // Lost the race to a concurrent Start: on Postgres the partial unique index
            // ux_load_run_active admits one non-terminal run.
            _db.Entry(run).State = EntityState.Detached;
            return null;
        }
    }

    public async Task<LoadRun?> GetActiveRunAsync(CancellationToken ct = default)
    {
        return await _db.LoadRuns.AsNoTracking()
            .Where(x => LoadRunStatus.NonTerminal.Contains(x.Status))
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<LoadRun?> GetActiveRunForScenarioAsync(string scenario, CancellationToken ct = default)
    {
        return await _db.LoadRuns.AsNoTracking()
            .Where(x => x.Scenario == scenario && LoadRunStatus.NonTerminal.Contains(x.Status))
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<LoadRun?> GetRunAsync(long id, CancellationToken ct = default)
    {
        return await _db.LoadRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<IReadOnlyList<LoadRun>> ListRunsAsync(int limit, long? before, CancellationToken ct = default)
    {
        var query = _db.LoadRuns.AsNoTracking();
        if (before is { } cursor)
        {
            query = query.Where(x => x.Id < cursor);
        }

        return await query.OrderByDescending(x => x.Id).Take(limit).ToListAsync(ct);
    }

    public async Task<LoadRun?> ClaimQueuedRunAsync(long id, string by, DateTimeOffset now, CancellationToken ct = default)
    {
        var run = await GetRunAsync(id, ct);
        if (run is null || run.Status != LoadRunStatus.Queued)
        {
            return null;
        }

        var next = run.FleetSizeRequested is null ? LoadRunStatus.GeneratorsUp : LoadRunStatus.FleetUp;
        var timeline = LoadJson.AppendTimeline(run.Timeline, now, next, by);

        // The claim: still queued, timeline unchanged since the read (so a concurrent
        // stop request's entry is never overwritten), and no other run non-terminal.
        var claimed = await _db.LoadRuns
            .Where(x => x.Id == id
                && x.Status == LoadRunStatus.Queued
                && x.Timeline == run.Timeline
                && !_db.LoadRuns.Any(o => o.Id != id && LoadRunStatus.NonTerminal.Contains(o.Status)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, next)
                .SetProperty(x => x.StartedAt, now)
                .SetProperty(x => x.Timeline, timeline), ct);

        return claimed == 1 ? await GetRunAsync(id, ct) : null;
    }

    public async Task<LoadRun?> TryAdvanceRunAsync(LoadRun expected, LoadRun next, CancellationToken ct = default)
    {
        var updated = await _db.LoadRuns
            .Where(x => x.Id == expected.Id
                && x.Status == expected.Status
                && x.Timeline == expected.Timeline)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, next.Status)
                .SetProperty(x => x.Timeline, next.Timeline)
                .SetProperty(x => x.RampStartedAt, next.RampStartedAt)
                .SetProperty(x => x.FinishedAt, next.FinishedAt)
                .SetProperty(x => x.FleetSizeBefore, next.FleetSizeBefore)
                .SetProperty(x => x.StopReason, next.StopReason)
                .SetProperty(x => x.Detail, next.Detail)
                .SetProperty(x => x.SpecToken, next.SpecToken), ct);

        return updated == 1 ? await GetRunAsync(expected.Id, ct) : null;
    }

    public async Task<bool> SetScenarioValidationAsync(string scenario, int scriptVersion, long runId, CancellationToken ct = default)
    {
        var updated = await _db.LoadScenarios
            .Where(x => x.Name == scenario && x.ScriptVersion == scriptVersion)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastValidationRunId, runId), ct);
        return updated == 1;
    }

    public async Task<LoadCheckInResult> CheckInAsync(long runId, string instanceId, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var run = await GetRunAsync(runId, ct);
            if (run is null)
            {
                return new LoadCheckInResult(LoadCheckInOutcome.NotFound, -1, null);
            }

            var checkins = LoadJson.ParseCheckins(run.Checkins);
            if (checkins.TryGetValue(instanceId, out var earlier) && LoadRunStatus.TakesRepeatCheckIn(run.Status))
            {
                return new LoadCheckInResult(LoadCheckInOutcome.Assigned, earlier, run);
            }

            if (run.Status != LoadRunStatus.GeneratorsUp)
            {
                return new LoadCheckInResult(LoadCheckInOutcome.NotAccepting, -1, run);
            }

            if (run.CheckedIn >= run.Generators)
            {
                return new LoadCheckInResult(LoadCheckInOutcome.Full, -1, run);
            }

            // The previous checked_in is the index; the update is conditional on it, so
            // of two instances that read the same value exactly one wins and the other
            // reads again.
            var index = run.CheckedIn;
            checkins[instanceId] = index;
            var json = LoadJson.WriteCheckins(checkins);

            var updated = await _db.LoadRuns
                .Where(x => x.Id == runId
                    && x.Status == LoadRunStatus.GeneratorsUp
                    && x.CheckedIn == index
                    && x.CheckedIn < x.Generators)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.CheckedIn, index + 1)
                    .SetProperty(x => x.Checkins, json), ct);

            if (updated == 1)
            {
                run.CheckedIn = index + 1;
                run.Checkins = json;
                return new LoadCheckInResult(LoadCheckInOutcome.Assigned, index, run);
            }
        }

        return new LoadCheckInResult(LoadCheckInOutcome.NotAccepting, -1, await GetRunAsync(runId, ct));
    }

    public async Task<LoadRun?> RequestStopAsync(long id, string by, DateTimeOffset now, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var run = await GetRunAsync(id, ct);
            if (run is null || LoadRunStatus.IsTerminal(run.Status) || run.StopRequestedAt is not null)
            {
                return run;
            }

            var timeline = LoadJson.AppendTimeline(run.Timeline, now, run.Status, by, "stop requested");
            var stamped = await _db.LoadRuns
                .Where(x => x.Id == id
                    && x.StopRequestedAt == null
                    && x.Timeline == run.Timeline
                    && LoadRunStatus.NonTerminal.Contains(x.Status))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.StopRequestedAt, now)
                    .SetProperty(x => x.Timeline, timeline), ct);
            if (stamped == 1)
            {
                break;
            }
        }

        return await GetRunAsync(id, ct);
    }

    public async Task<bool> SetVerdictAsync(long id, string? verdict, CancellationToken ct = default)
    {
        var updated = await _db.LoadRuns
            .Where(x => x.Id == id && LoadRunStatus.Terminal.Contains(x.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Verdict, verdict), ct);
        return updated == 1;
    }

    public async Task<bool> DeleteRunAsync(long id, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var deleted = await _db.LoadRuns
            .Where(x => x.Id == id && LoadRunStatus.Terminal.Contains(x.Status))
            .ExecuteDeleteAsync(ct);
        if (deleted == 0)
        {
            return false;
        }

        await _db.LoadRunStats.Where(x => x.RunId == id).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    // ---------- stats ----------

    public async Task<LoadRunStat> AppendStatAsync(LoadRunStat stat, CancellationToken ct = default)
    {
        _db.LoadRunStats.Add(stat);
        await _db.SaveChangesAsync(ct);
        _db.Entry(stat).State = EntityState.Detached;
        return stat;
    }

    public async Task UpdatePeaksAsync(long runId, IReadOnlyDictionary<string, double> sample, CancellationToken ct = default)
    {
        if (sample.Count == 0)
        {
            return;
        }

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var before = await _db.LoadRuns.AsNoTracking()
                .Where(x => x.Id == runId)
                .Select(x => x.Peaks)
                .FirstOrDefaultAsync(ct);
            if (before is null)
            {
                return;
            }

            var after = LoadJson.MergePeaks(before, sample);
            if (string.Equals(after, before, StringComparison.Ordinal))
            {
                return;
            }

            var updated = await _db.LoadRuns
                .Where(x => x.Id == runId && x.Peaks == before)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Peaks, after), ct);
            if (updated == 1)
            {
                return;
            }
        }
    }

    public async Task<IReadOnlyList<LoadRunStat>> ListStatsAsync(
        long runId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? source,
        int? index,
        CancellationToken ct = default)
    {
        var query = _db.LoadRunStats.AsNoTracking().Where(x => x.RunId == runId);
        if (source is not null)
        {
            query = query.Where(x => x.Source == source);
        }

        if (index is { } i)
        {
            query = query.Where(x => x.Index == i);
        }

        if (_db.Database.IsNpgsql())
        {
            if (from is { } f)
            {
                query = query.Where(x => x.At >= f);
            }

            if (to is { } t)
            {
                query = query.Where(x => x.At <= t);
            }
        }

        var rows = await query.OrderBy(x => x.Id).ToListAsync(ct);
        return rows
            .Where(x => (from is null || x.At >= from) && (to is null || x.At <= to))
            .OrderBy(x => x.At)
            .ThenBy(x => x.Id)
            .ToList();
    }

    public async Task<IReadOnlyList<LoadRunStat>> GetLatestGeneratorStatsAsync(long runId, CancellationToken ct = default)
    {
        var latestIds = _db.LoadRunStats
            .Where(x => x.RunId == runId && x.Source == LoadStatSource.Generator)
            .GroupBy(x => x.Index)
            .Select(g => g.Max(x => x.Id));
        var rows = await _db.LoadRunStats.AsNoTracking()
            .Where(x => latestIds.Contains(x.Id))
            .ToListAsync(ct);
        return rows.OrderBy(x => x.Index).ToList();
    }
}
