using Gateway.Api.Data;
using Gateway.Api.Load;
using Gateway.Api.Management;

namespace Gateway.Api.Tests;

/// <summary>
/// In-memory <see cref="ILoadStore"/> for tests: keeps scenarios, script history, runs
/// and stats so a test can drive the load model without a database. Mirrors the EF
/// store's rules: one non-terminal run, conditional claim and check-in, history that
/// outlives a deleted scenario, and stats removed with their run. Every read returns a
/// copy, so a caller cannot change the stored row by accident.
/// </summary>
public sealed class FakeLoadStore : ILoadStore
{
    private readonly object _gate = new();
    private long _nextRunId = 1;
    private long _nextStatId = 1;

    public List<LoadScenario> Scenarios { get; } = new();
    public List<LoadScenarioVersion> Versions { get; } = new();
    public List<LoadRun> Runs { get; } = new();
    public List<LoadRunStat> Stats { get; } = new();

    /// <summary>Seed a run with its id kept (or assigned when 0); returns the stored row.</summary>
    public LoadRun Seed(LoadRun run)
    {
        lock (_gate)
        {
            if (run.Id == 0)
            {
                run.Id = _nextRunId;
            }

            _nextRunId = Math.Max(_nextRunId, run.Id + 1);
            Runs.Add(run);
            return run;
        }
    }

    /// <summary>Seed a scenario and, when given, its history rows.</summary>
    public void Seed(LoadScenario scenario, params LoadScenarioVersion[] versions)
    {
        lock (_gate)
        {
            Scenarios.Add(scenario);
            Versions.AddRange(versions);
        }
    }

    /// <summary>The stored run with <paramref name="id"/> (not a copy), for assertions.</summary>
    public LoadRun Run(long id)
    {
        lock (_gate)
        {
            return Runs.Single(r => r.Id == id);
        }
    }

    // ---------- scenarios ----------

    public Task<IReadOnlyList<LoadScenario>> ListScenariosAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<LoadScenario>>(
                Scenarios.OrderBy(s => s.Name, StringComparer.Ordinal).Select(Copy).ToList());
        }
    }

    public Task<LoadScenario?> GetScenarioAsync(string name, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var row = Scenarios.FirstOrDefault(s => s.Name == name);
            return Task.FromResult(row is null ? null : Copy(row));
        }
    }

    public Task UpsertScenarioAsync(LoadScenario scenario, LoadScenarioVersion? version = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (version is not null && Versions.Any(v => v.Scenario == version.Scenario && v.Version == version.Version))
            {
                throw new InvalidOperationException($"duplicate script version {version.Scenario}/{version.Version}");
            }

            Scenarios.RemoveAll(s => s.Name == scenario.Name);
            Scenarios.Add(Copy(scenario));
            if (version is not null)
            {
                Versions.Add(Copy(version));
            }

            return Task.CompletedTask;
        }
    }

    public Task DeleteScenarioAsync(string name, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Scenarios.RemoveAll(s => s.Name == name);
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<LoadScenarioVersion>> ListScenarioVersionsAsync(string scenario, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<LoadScenarioVersion>>(
                Versions.Where(v => v.Scenario == scenario).OrderByDescending(v => v.Version).Select(Copy).ToList());
        }
    }

    public Task<LoadScenarioVersion?> GetScenarioVersionAsync(string scenario, int version, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var row = Versions.FirstOrDefault(v => v.Scenario == scenario && v.Version == version);
            return Task.FromResult(row is null ? null : Copy(row));
        }
    }

    // ---------- runs ----------

    public Task<LoadRun?> TryCreateRunAsync(LoadRun run, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (Runs.Any(r => !LoadRunStatus.IsTerminal(r.Status)))
            {
                return Task.FromResult<LoadRun?>(null);
            }

            run.Id = _nextRunId++;
            Runs.Add(Copy(run));
            return Task.FromResult<LoadRun?>(Copy(run));
        }
    }

    public Task<LoadRun?> GetActiveRunAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var run = Runs.Where(r => !LoadRunStatus.IsTerminal(r.Status)).OrderByDescending(r => r.Id).FirstOrDefault();
            return Task.FromResult(run is null ? null : Copy(run));
        }
    }

    public Task<LoadRun?> GetActiveRunForScenarioAsync(string scenario, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var run = Runs
                .Where(r => r.Scenario == scenario && !LoadRunStatus.IsTerminal(r.Status))
                .OrderByDescending(r => r.Id)
                .FirstOrDefault();
            return Task.FromResult(run is null ? null : Copy(run));
        }
    }

    public Task<LoadRun?> GetRunAsync(long id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var run = Runs.FirstOrDefault(r => r.Id == id);
            return Task.FromResult(run is null ? null : Copy(run));
        }
    }

    public Task<IReadOnlyList<LoadRun>> ListRunsAsync(int limit, long? before, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<LoadRun>>(Runs
                .Where(r => before is null || r.Id < before)
                .OrderByDescending(r => r.Id)
                .Take(limit)
                .Select(Copy)
                .ToList());
        }
    }

    public Task<LoadRun?> ClaimQueuedRunAsync(long id, string by, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var run = Runs.FirstOrDefault(r => r.Id == id);
            if (run is null
                || run.Status != LoadRunStatus.Queued
                || Runs.Any(r => r.Id != id && !LoadRunStatus.IsTerminal(r.Status)))
            {
                return Task.FromResult<LoadRun?>(null);
            }

            run.Status = run.FleetSizeRequested is null ? LoadRunStatus.GeneratorsUp : LoadRunStatus.FleetUp;
            run.StartedAt = now;
            run.Timeline = LoadJson.AppendTimeline(run.Timeline, now, run.Status, by);
            return Task.FromResult<LoadRun?>(Copy(run));
        }
    }

    /// <summary>Calls of <see cref="TryAdvanceRunAsync"/> that matched nothing.</summary>
    public int RefusedAdvances { get; private set; }

    public Task<LoadRun?> TryAdvanceRunAsync(LoadRun expected, LoadRun next, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var run = Runs.FirstOrDefault(r => r.Id == expected.Id);
            if (run is null || run.Status != expected.Status || run.Timeline != expected.Timeline)
            {
                RefusedAdvances++;
                return Task.FromResult<LoadRun?>(null);
            }

            run.Status = next.Status;
            run.Timeline = next.Timeline;
            run.RampStartedAt = next.RampStartedAt;
            run.FinishedAt = next.FinishedAt;
            run.FleetSizeBefore = next.FleetSizeBefore;
            run.StopReason = next.StopReason;
            run.Detail = next.Detail;
            run.SpecToken = next.SpecToken;
            return Task.FromResult<LoadRun?>(Copy(run));
        }
    }

    public Task<bool> SetScenarioValidationAsync(string scenario, int scriptVersion, long runId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var row = Scenarios.FirstOrDefault(s => s.Name == scenario && s.ScriptVersion == scriptVersion);
            if (row is null)
            {
                return Task.FromResult(false);
            }

            row.LastValidationRunId = runId;
            return Task.FromResult(true);
        }
    }

    public Task<LoadCheckInResult> CheckInAsync(long runId, string instanceId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var run = Runs.FirstOrDefault(r => r.Id == runId);
            if (run is null)
            {
                return Task.FromResult(new LoadCheckInResult(LoadCheckInOutcome.NotFound, -1, null));
            }

            var checkins = LoadJson.ParseCheckins(run.Checkins);
            if (checkins.TryGetValue(instanceId, out var earlier) && LoadRunStatus.TakesRepeatCheckIn(run.Status))
            {
                return Task.FromResult(new LoadCheckInResult(LoadCheckInOutcome.Assigned, earlier, Copy(run)));
            }

            if (run.Status != LoadRunStatus.GeneratorsUp)
            {
                return Task.FromResult(new LoadCheckInResult(LoadCheckInOutcome.NotAccepting, -1, Copy(run)));
            }

            if (run.CheckedIn >= run.Generators)
            {
                return Task.FromResult(new LoadCheckInResult(LoadCheckInOutcome.Full, -1, Copy(run)));
            }

            var index = run.CheckedIn;
            checkins[instanceId] = index;
            run.Checkins = LoadJson.WriteCheckins(checkins);
            run.CheckedIn = index + 1;
            return Task.FromResult(new LoadCheckInResult(LoadCheckInOutcome.Assigned, index, Copy(run)));
        }
    }

    public Task<LoadRun?> RequestStopAsync(long id, string by, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var run = Runs.FirstOrDefault(r => r.Id == id);
            if (run is null)
            {
                return Task.FromResult<LoadRun?>(null);
            }

            if (!LoadRunStatus.IsTerminal(run.Status) && run.StopRequestedAt is null)
            {
                run.StopRequestedAt = now;
                run.Timeline = LoadJson.AppendTimeline(run.Timeline, now, run.Status, by, "stop requested");
            }

            return Task.FromResult<LoadRun?>(Copy(run));
        }
    }

    public Task<bool> SetVerdictAsync(long id, string? verdict, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var run = Runs.FirstOrDefault(r => r.Id == id);
            if (run is null || !LoadRunStatus.IsTerminal(run.Status))
            {
                return Task.FromResult(false);
            }

            run.Verdict = verdict;
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteRunAsync(long id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var removed = Runs.RemoveAll(r => r.Id == id && LoadRunStatus.IsTerminal(r.Status));
            if (removed == 0)
            {
                return Task.FromResult(false);
            }

            Stats.RemoveAll(s => s.RunId == id);
            return Task.FromResult(true);
        }
    }

    // ---------- stats ----------

    public Task<LoadRunStat> AppendStatAsync(LoadRunStat stat, CancellationToken ct = default)
    {
        lock (_gate)
        {
            stat.Id = _nextStatId++;
            Stats.Add(Copy(stat));
            return Task.FromResult(Copy(stat));
        }
    }

    public Task UpdatePeaksAsync(long runId, IReadOnlyDictionary<string, double> sample, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var run = Runs.FirstOrDefault(r => r.Id == runId);
            if (run is not null && sample.Count > 0)
            {
                run.Peaks = LoadJson.MergePeaks(run.Peaks, sample);
            }

            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<LoadRunStat>> ListStatsAsync(
        long runId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? source,
        int? index,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<LoadRunStat>>(Stats
                .Where(s => s.RunId == runId
                    && (source is null || s.Source == source)
                    && (index is null || s.Index == index)
                    && (from is null || s.At >= from)
                    && (to is null || s.At <= to))
                .OrderBy(s => s.At)
                .ThenBy(s => s.Id)
                .Select(Copy)
                .ToList());
        }
    }

    public Task<IReadOnlyList<LoadRunStat>> GetLatestGeneratorStatsAsync(long runId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<LoadRunStat>>(Stats
                .Where(s => s.RunId == runId && s.Source == LoadStatSource.Generator)
                .GroupBy(s => s.Index)
                .Select(g => g.OrderByDescending(s => s.Id).First())
                .OrderBy(s => s.Index)
                .Select(Copy)
                .ToList());
        }
    }

    // ---------- copies ----------

    private static T Copy<T>(T source)
        where T : class, new()
    {
        var copy = new T();
        foreach (var property in typeof(T).GetProperties().Where(p => p.CanRead && p.CanWrite))
        {
            property.SetValue(copy, property.GetValue(source));
        }

        return copy;
    }
}
