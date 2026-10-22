using Gateway.Api.Data;
using Gateway.Api.Management;

namespace Gateway.Api.Tests;

/// <summary>
/// In-memory <see cref="IJobStore"/> for tests: keeps job rows, script history and
/// runs so a test can drive the job model without a database. Mirrors the EF
/// store's rules: one queued-or-running run per job, conditional cancel, and run
/// history that outlives a deleted job.
/// </summary>
public sealed class FakeJobStore : IJobStore
{
    private readonly object _gate = new();
    private long _nextRunId = 1;

    public List<Job> Jobs { get; } = new();
    public List<JobScriptVersion> ScriptVersions { get; } = new();
    public List<JobRun> Runs { get; } = new();

    public Task<IReadOnlyList<Job>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Job>>(
                Jobs.OrderBy(j => j.Name, StringComparer.Ordinal).Select(Copy).ToList());
        }
    }

    public Task<Job?> GetAsync(string name, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var job = Jobs.FirstOrDefault(j => j.Name == name);
            return Task.FromResult(job is null ? null : Copy(job));
        }
    }

    public Task UpsertAsync(Job job, JobScriptVersion? scriptVersion = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Jobs.RemoveAll(j => j.Name == job.Name);
            Jobs.Add(Copy(job));
            if (scriptVersion is not null)
            {
                if (ScriptVersions.Any(v => v.Job == scriptVersion.Job && v.Version == scriptVersion.Version))
                {
                    throw new InvalidOperationException(
                        $"duplicate script version {scriptVersion.Job}/{scriptVersion.Version}");
                }

                ScriptVersions.Add(scriptVersion);
            }

            return Task.CompletedTask;
        }
    }

    public Task DeleteAsync(string name, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Jobs.RemoveAll(j => j.Name == name);
            ScriptVersions.RemoveAll(v => v.Job == name);
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<JobScriptVersion>> ListScriptVersionsAsync(string job, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<JobScriptVersion>>(
                ScriptVersions.Where(v => v.Job == job).OrderByDescending(v => v.Version).ToList());
        }
    }

    public Task<JobScriptVersion?> GetScriptVersionAsync(string job, int version, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(ScriptVersions.FirstOrDefault(v => v.Job == job && v.Version == version));
        }
    }

    public Task<JobRun?> TryEnqueueRunAsync(JobRun run, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (Runs.Any(r => r.Job == run.Job && JobRunStatus.IsActive(r.Status)))
            {
                return Task.FromResult<JobRun?>(null);
            }

            run.Id = _nextRunId++;
            Runs.Add(run);
            return Task.FromResult<JobRun?>(Copy(run));
        }
    }

    public Task<JobRun?> GetActiveRunAsync(string job, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var run = Runs.LastOrDefault(r => r.Job == job && JobRunStatus.IsActive(r.Status));
            return Task.FromResult(run is null ? null : Copy(run));
        }
    }

    public Task<IReadOnlyDictionary<string, JobRun>> GetLastRunsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyDictionary<string, JobRun> last = Runs
                .GroupBy(r => r.Job)
                .ToDictionary(g => g.Key, g => Copy(g.MaxBy(r => r.Id)!), StringComparer.Ordinal);
            return Task.FromResult(last);
        }
    }

    public Task<JobRun?> GetRunAsync(long id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var run = Runs.FirstOrDefault(r => r.Id == id);
            return Task.FromResult(run is null ? null : Copy(run));
        }
    }

    public Task<IReadOnlyList<JobRun>> ListRunsAsync(string job, int limit, long? before, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<JobRun>>(Runs
                .Where(r => r.Job == job && (before is null || r.Id < before))
                .OrderByDescending(r => r.Id)
                .Take(limit)
                .Select(Copy)
                .ToList());
        }
    }

    public Task<JobRun?> CancelRunAsync(long id, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var run = Runs.FirstOrDefault(r => r.Id == id);
            if (run is null)
            {
                return Task.FromResult<JobRun?>(null);
            }

            if (run.Status == JobRunStatus.Queued)
            {
                run.Status = JobRunStatus.Cancelled;
                run.CancelRequestedAt = now;
                run.FinishedAt = now;
            }
            else if (run.Status == JobRunStatus.Running && run.CancelRequestedAt is null)
            {
                run.CancelRequestedAt = now;
            }

            return Task.FromResult<JobRun?>(Copy(run));
        }
    }

    public Task<IReadOnlyList<Job>> ListDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Job>>(Jobs
                .Where(j => j.Enabled && j.NextRunAt is { } at && at <= now)
                .OrderBy(j => j.NextRunAt)
                .ThenBy(j => j.Name, StringComparer.Ordinal)
                .Select(Copy)
                .ToList());
        }
    }

    public Task<JobRun?> TryClaimScheduledRunAsync(
        string job, DateTimeOffset dueAt, DateTimeOffset? nextRunAt, JobRun run, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var row = Jobs.FirstOrDefault(j => j.Name == job);
            if (row is null || !row.Enabled || row.NextRunAt != dueAt
                || Runs.Any(r => r.Job == job && JobRunStatus.IsActive(r.Status)))
            {
                return Task.FromResult<JobRun?>(null);
            }

            row.NextRunAt = nextRunAt;
            run.Id = _nextRunId++;
            Runs.Add(run);
            return Task.FromResult<JobRun?>(Copy(run));
        }
    }

    public Task<IReadOnlyList<JobRun>> ListRunsByStatusAsync(string status, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<JobRun>>(
                Runs.Where(r => r.Status == status).OrderBy(r => r.Id).Select(Copy).ToList());
        }
    }

    public Task<bool> TryClaimQueuedRunAsync(long id, string instanceId, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var run = Runs.FirstOrDefault(r => r.Id == id && r.Status == JobRunStatus.Queued);
            if (run is null)
            {
                return Task.FromResult(false);
            }

            run.Status = JobRunStatus.Running;
            run.InstanceId = instanceId;
            run.StartedAt = now;
            return Task.FromResult(true);
        }
    }

    public Task SetRunImageDigestAsync(long id, string digest, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var run = Runs.FirstOrDefault(r => r.Id == id);
            if (run is not null)
            {
                run.ImageDigest = digest;
            }

            return Task.CompletedTask;
        }
    }

    public Task<bool> FinishRunAsync(
        long id,
        string instanceId,
        string status,
        int? exitCode,
        string? detail,
        DateTimeOffset finishedAt,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var run = Runs.FirstOrDefault(r => r.Id == id
                && r.InstanceId == instanceId
                && r.Status is JobRunStatus.Running or JobRunStatus.Lost);
            if (run is null)
            {
                return Task.FromResult(false);
            }

            run.Status = status;
            run.ExitCode = exitCode;
            run.Detail = detail;
            run.FinishedAt = finishedAt;
            return Task.FromResult(true);
        }
    }

    public Task<bool> MarkRunLostAsync(
        long id, string instanceId, DateTimeOffset now, string detail, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var run = Runs.FirstOrDefault(r => r.Id == id
                && r.InstanceId == instanceId
                && r.Status == JobRunStatus.Running);
            if (run is null)
            {
                return Task.FromResult(false);
            }

            run.Status = JobRunStatus.Lost;
            run.Detail = detail;
            run.FinishedAt = now;
            return Task.FromResult(true);
        }
    }

    /// <summary>Snapshot of one run by id, for assertions.</summary>
    public JobRun Run(long id)
    {
        lock (_gate)
        {
            return Copy(Runs.Single(r => r.Id == id));
        }
    }

    /// <summary>Snapshot of every run, for assertions.</summary>
    public IReadOnlyList<JobRun> AllRuns()
    {
        lock (_gate)
        {
            return Runs.Select(Copy).ToList();
        }
    }

    /// <summary>Snapshot of one job by name, for assertions.</summary>
    public Job JobRow(string name)
    {
        lock (_gate)
        {
            return Copy(Jobs.Single(j => j.Name == name));
        }
    }

    private static Job Copy(Job j) => new()
    {
        Name = j.Name,
        Schedule = j.Schedule,
        TimeZone = j.TimeZone,
        Script = j.Script,
        ScriptVersion = j.ScriptVersion,
        Image = j.Image,
        Tag = j.Tag,
        EnvSecretRef = j.EnvSecretRef,
        TimeoutSeconds = j.TimeoutSeconds,
        Enabled = j.Enabled,
        NextRunAt = j.NextRunAt,
        UpdatedBy = j.UpdatedBy,
        UpdatedAt = j.UpdatedAt,
    };

    private static JobRun Copy(JobRun r) => new()
    {
        Id = r.Id,
        Job = r.Job,
        Trigger = r.Trigger,
        Actor = r.Actor,
        Status = r.Status,
        InstanceId = r.InstanceId,
        ImageDigest = r.ImageDigest,
        ScriptVersion = r.ScriptVersion,
        ExitCode = r.ExitCode,
        QueuedAt = r.QueuedAt,
        StartedAt = r.StartedAt,
        FinishedAt = r.FinishedAt,
        CancelRequestedAt = r.CancelRequestedAt,
        Detail = r.Detail,
    };
}
