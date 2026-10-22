using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Gateway.Api.Containers;
using Gateway.Api.Data;
using Gateway.Api.Instances;
using Gateway.Api.Management;
using Gateway.Api.RealTime;
using Gateway.Api.Reconcile;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Jobs;

/// <summary>
/// The job executor (docs/jobs.md section 3). Ticks every
/// <see cref="JobSchedulerOptions.TickInterval"/> on every instance and does two
/// things.
/// <para>
/// <b>Leader duties</b>, only while <see cref="ILeaderStatus"/> says this instance is
/// the leader and the evaluation is fresher than the heartbeat stale threshold:
/// claim due scheduled runs (one run however many slots were missed), mark running
/// runs on instances with a stale heartbeat as <c>lost</c>, and claim queued runs
/// oldest first for this instance with a conditional update, up to
/// <see cref="JobSchedulerOptions.MaxConcurrent"/> runs on this instance. Every claim
/// is atomic in the store, so two leaders ticking together start a run once.
/// </para>
/// <para>
/// <b>Own duties</b>, every tick on every instance: drive each run this instance owns
/// and is not driving yet on a background task (resolve env, pull, run the
/// container, finalise), and poll <c>cancel_requested_at</c> for the runs it drives.
/// A run this process did not claim (it owned the run before a restart) is
/// re-attached by label instead of started again.
/// </para>
/// <para>
/// The run's container is never tied to the host's stopping token: a gateway that
/// stops mid-run leaves the container running and the next process re-attaches.
/// </para>
/// </summary>
public sealed class JobSchedulerService : BackgroundService
{
    /// <summary>The <c>actor</c> recorded on runs the schedule enqueues.</summary>
    public const string ScheduleActor = "schedule";

    /// <summary>Largest <c>detail</c> stored on a run, in UTF-8 bytes (docs/jobs.md section 2).</summary>
    public const int MaxDetailBytes = 2048;

    private readonly IContainerRuntime _runtime;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISecretEnvProvider _env;
    private readonly ILeaderStatus _leader;
    private readonly InstanceMetadataProvider _metadata;
    private readonly JobSchedulerOptions _options;
    private readonly ReconcilerOptions _reconciler;
    private readonly IJobMetrics _metrics;
    private readonly ILogger<JobSchedulerService> _logger;
    private readonly TimeProvider _time;
    private readonly MigrationReadinessGate? _migrationGate;
    private readonly IChannelEventPublisher? _publisher;

    // Runs this process is driving, keyed by run id. A driver removes itself when its
    // run is finalised.
    private readonly ConcurrentDictionary<long, Driver> _drivers = new();

    // Runs this process claimed from queued. An owned running run that is not in here
    // was claimed before a restart, so it is re-attached rather than started.
    private readonly ConcurrentDictionary<long, byte> _claimed = new();

    // Job log groups whose retention has been ensured by this process.
    private readonly HashSet<string> _retentionEnsured = new(StringComparer.Ordinal);

    private sealed class Driver
    {
        public CancellationTokenSource Cancel { get; } = new();

        public Task Task { get; set; } = Task.CompletedTask;
    }

    private readonly record struct Outcome(string Status, int? ExitCode, string? Detail);

    public JobSchedulerService(
        IContainerRuntime runtime,
        IServiceScopeFactory scopeFactory,
        ISecretEnvProvider env,
        ILeaderStatus leader,
        InstanceMetadataProvider metadata,
        JobSchedulerOptions options,
        ReconcilerOptions reconciler,
        IJobMetrics metrics,
        ILogger<JobSchedulerService> logger,
        TimeProvider? time = null,
        MigrationReadinessGate? migrationGate = null,
        IChannelEventPublisher? publisher = null)
    {
        _runtime = runtime;
        _scopeFactory = scopeFactory;
        _env = env;
        _leader = leader;
        _metadata = metadata;
        _options = options;
        _reconciler = reconciler;
        _metrics = metrics;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _migrationGate = migrationGate;
        _publisher = publisher;
    }

    /// <summary>Ids of the runs this process is driving right now.</summary>
    public IReadOnlyCollection<long> DrivenRunIds => _drivers.Keys.ToArray();

    /// <summary>Wait until every run this process is driving has been finalised.</summary>
    public Task WaitForDriversAsync() => Task.WhenAll(_drivers.Values.Select(d => d.Task).ToArray());

    /// <summary>The container name of a run: <c>job-{name}-{runId}</c>.</summary>
    public static string ContainerName(string job, long runId) =>
        string.Create(CultureInfo.InvariantCulture, $"job-{job}-{runId}");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_migrationGate is not null)
        {
            try
            {
                await _migrationGate.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }

        _logger.LogInformation(
            "Job scheduler running every {Interval}, at most {Max} concurrent run(s) on this instance.",
            _options.TickInterval, _options.MaxConcurrent);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job scheduler tick failed");
            }

            try
            {
                await Task.Delay(_options.TickInterval, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One tick: leader duties when this instance is a fresh leader, then own duties.
    /// Public so tests drive ticks directly. A failing step is logged and the next
    /// step still runs.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        var self = (await _metadata.GetAsync(ct)).InstanceId;
        var now = _time.GetUtcNow();

        if (IsFreshLeader(now))
        {
            await GuardAsync("claim due scheduled runs", () => ClaimDueScheduledRunsAsync(now, ct));
            await GuardAsync("mark lost runs", () => MarkLostRunsAsync(self, now, ct));
            await GuardAsync("claim queued runs", () => ClaimQueuedRunsAsync(self, now, ct));
        }

        await GuardAsync("drive own runs", () => DriveOwnRunsAsync(self, ct));
        await GuardAsync("poll cancel requests", () => PollCancelRequestsAsync(ct));
    }

    private bool IsFreshLeader(DateTimeOffset now) =>
        _leader.Current.IsLeader
        && _leader.EvaluatedAt is { } at
        && now - at < _reconciler.InstanceStaleThreshold;

    private async Task GuardAsync(string step, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Job scheduler failed to {Step}; retrying next tick.", step);
        }
    }

    // ---------- leader duties ----------

    private async Task ClaimDueScheduledRunsAsync(DateTimeOffset now, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IJobStore>();

        foreach (var job in await store.ListDueAsync(now, ct))
        {
            if (await store.GetActiveRunAsync(job.Name, ct) is not null)
            {
                // One run at a time: the job stays due and runs once when the active
                // run ends.
                continue;
            }

            // The next occurrence after now, not after the missed slot: a fleet that
            // was down for several periods runs the job once.
            DateTimeOffset? next;
            try
            {
                next = JobSchedule.ComputeNextRunAt(job, now);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "Job {Job} has an unusable schedule; it runs now and is not rescheduled.", job.Name);
                next = null;
            }

            var claimed = await store.TryClaimScheduledRunAsync(job.Name, job.NextRunAt!.Value, next, new JobRun
            {
                Job = job.Name,
                Trigger = JobRunTrigger.Schedule,
                Actor = ScheduleActor,
                Status = JobRunStatus.Queued,
                ScriptVersion = job.ScriptVersion,
                QueuedAt = now,
            }, ct);

            if (claimed is not null)
            {
                _logger.LogInformation(
                    "Queued scheduled run {RunId} of job {Job}; next run at {Next}.", claimed.Id, job.Name, next);
                Broadcast(claimed);
            }
        }
    }

    private async Task MarkLostRunsAsync(string self, DateTimeOffset now, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IJobStore>();

        // This instance's own runs are never lost to itself: it re-attaches them.
        var others = (await store.ListRunsByStatusAsync(JobRunStatus.Running, ct))
            .Where(r => r.InstanceId is not null && r.InstanceId != self)
            .ToList();
        if (others.Count == 0)
        {
            return;
        }

        var statusStore = scope.ServiceProvider.GetRequiredService<IInstanceStatusStore>();
        var threshold = _reconciler.InstanceStaleThreshold;
        var live = (await statusStore.GetAllAsync(ct))
            .Where(s => now - s.HeartbeatAt <= threshold)
            .Select(s => s.InstanceId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var run in others.Where(r => !live.Contains(r.InstanceId!)))
        {
            var detail = string.Create(CultureInfo.InvariantCulture,
                $"instance {run.InstanceId} has no heartbeat within {threshold.TotalSeconds:0}s; the run's outcome is unknown");
            if (!await store.MarkRunLostAsync(run.Id, run.InstanceId!, now, detail, ct))
            {
                continue;
            }

            _logger.LogWarning("Marked run {RunId} of job {Job} lost: {Detail}", run.Id, run.Job, detail);
            _metrics.RunFinished(run.Job, JobRunStatus.Lost, run.StartedAt is { } started ? now - started : null);
            Broadcast(await store.GetRunAsync(run.Id, ct));
        }
    }

    private async Task ClaimQueuedRunsAsync(string self, DateTimeOffset now, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IJobStore>();

        var owned = (await store.ListRunsByStatusAsync(JobRunStatus.Running, ct))
            .Count(r => r.InstanceId == self);
        var free = _options.MaxConcurrent - owned;
        if (free <= 0)
        {
            return;
        }

        foreach (var run in await store.ListRunsByStatusAsync(JobRunStatus.Queued, ct))
        {
            if (free <= 0)
            {
                break;
            }

            if (!await store.TryClaimQueuedRunAsync(run.Id, self, now, ct))
            {
                // Another scheduler won it, or it was cancelled in the meantime.
                continue;
            }

            _claimed[run.Id] = 0;
            free--;
            _logger.LogInformation("Claimed run {RunId} of job {Job} for this instance.", run.Id, run.Job);
            Broadcast(await store.GetRunAsync(run.Id, ct));
        }
    }

    // ---------- own duties ----------

    private async Task DriveOwnRunsAsync(string self, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IJobStore>();

        var owned = (await store.ListRunsByStatusAsync(JobRunStatus.Running, ct))
            .Where(r => r.InstanceId == self);
        foreach (var run in owned)
        {
            if (_drivers.ContainsKey(run.Id))
            {
                continue;
            }

            if (_drivers.Count >= _options.MaxConcurrent)
            {
                break;
            }

            var reattach = !_claimed.ContainsKey(run.Id);
            var driver = new Driver();
            if (run.CancelRequestedAt is not null)
            {
                driver.Cancel.Cancel();
            }

            // The task waits on the gate until it is registered, so its own removal in
            // the finally can never run before the add.
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            driver.Task = Task.Run(async () =>
            {
                await gate.Task;
                await DriveAsync(self, run, reattach, driver);
            }, CancellationToken.None);
            _drivers[run.Id] = driver;
            gate.SetResult();
        }
    }

    private async Task PollCancelRequestsAsync(CancellationToken ct)
    {
        if (_drivers.IsEmpty)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IJobStore>();

        foreach (var (id, driver) in _drivers)
        {
            if (driver.Cancel.IsCancellationRequested)
            {
                continue;
            }

            var run = await store.GetRunAsync(id, ct);
            if (run?.CancelRequestedAt is not null)
            {
                _logger.LogInformation("Cancel requested for run {RunId} of job {Job}; killing its container.", id, run.Job);
                driver.Cancel.Cancel();
            }
        }
    }

    private async Task DriveAsync(string self, JobRun run, bool reattach, Driver driver)
    {
        try
        {
            var outcome = reattach
                ? await ReattachAsync(run, driver.Cancel.Token)
                : await LaunchAsync(run, driver.Cancel.Token);
            await FinishAsync(self, run, outcome);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Driving run {RunId} of job {Job} failed.", run.Id, run.Job);
            try
            {
                await FinishAsync(self, run, new Outcome(JobRunStatus.Failed, null, $"executor error: {ex.Message}"));
            }
            catch (Exception finishEx)
            {
                _logger.LogError(finishEx, "Could not finalise run {RunId}; it stays running until re-attached.", run.Id);
            }
        }
        finally
        {
            _drivers.TryRemove(run.Id, out _);
            _claimed.TryRemove(run.Id, out _);
        }
    }

    /// <summary>
    /// Start a run this process claimed: resolve env, pull, run the container. Every
    /// failure before the container starts finalises the run as failed with a detail
    /// that names the cause; nothing executes.
    /// </summary>
    private async Task<Outcome> LaunchAsync(JobRun run, CancellationToken cancel)
    {
        Job? job;
        using (var scope = _scopeFactory.CreateScope())
        {
            job = await scope.ServiceProvider.GetRequiredService<IJobStore>().GetAsync(run.Job, CancellationToken.None);
        }

        if (job is null)
        {
            return new Outcome(JobRunStatus.Failed, null, $"job '{run.Job}' no longer exists");
        }

        if (cancel.IsCancellationRequested)
        {
            return CancelledBeforeStart();
        }

        IReadOnlyDictionary<string, string> secretEnv;
        try
        {
            secretEnv = await _env.GetEnvAsync(job.EnvSecretRef, cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return CancelledBeforeStart();
        }
        catch (Exception ex)
        {
            return new Outcome(JobRunStatus.Failed, null, SecretFailureDetail(job.EnvSecretRef, ex));
        }

        string digest;
        try
        {
            digest = await _runtime.PullImageAsync(job.Image, job.Tag, cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return CancelledBeforeStart();
        }
        catch (Exception ex)
        {
            return new Outcome(JobRunStatus.Failed, null, $"image pull failed for {job.Image}:{job.Tag}: {ex.Message}");
        }

        using (var scope = _scopeFactory.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IJobStore>()
                .SetRunImageDigestAsync(run.Id, digest, CancellationToken.None);
        }

        if (cancel.IsCancellationRequested)
        {
            return CancelledBeforeStart();
        }

        var identity = await _metadata.GetAsync(CancellationToken.None);
        var spec = new JobContainerSpec(
            Name: ContainerName(job.Name, run.Id),
            Image: job.Image,
            Digest: digest,
            EnvVars: JobEnv.Build(secretEnv, job, run),
            Network: _reconciler.Network,
            LogConfig: LogConfigFactory.ForJob(job.Name, run.Id, identity.Region, _reconciler.LogDriver),
            MemoryLimitBytes: _options.MemoryLimitBytes,
            Labels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ContainerLabels.Job] = job.Name,
                [ContainerLabels.JobRun] = run.Id.ToString(CultureInfo.InvariantCulture),
            });

        _logger.LogInformation("Starting run {RunId} of job {Job} from {Image}@{Digest}.", run.Id, job.Name, job.Image, digest);

        JobRunResult result;
        try
        {
            result = await _runtime.RunJobContainerAsync(spec, TimeSpan.FromSeconds(job.TimeoutSeconds), cancel);
        }
        catch (Exception ex)
        {
            return new Outcome(JobRunStatus.Failed, null, $"container run failed: {ex.Message}");
        }

        // awslogs-create-group made the group when the container started, so retention
        // can be set now.
        await EnsureRetentionAsync(job.Name);
        return Classify(result, job.TimeoutSeconds);
    }

    /// <summary>
    /// Resume a run this instance owned before a restart: find its container by label
    /// and wait on it for what is left of the timeout. No container means the outcome
    /// cannot be known, so the run is lost.
    /// </summary>
    private async Task<Outcome> ReattachAsync(JobRun run, CancellationToken cancel)
    {
        var container = await _runtime.FindJobContainerAsync(run.Id, CancellationToken.None);
        if (container is null)
        {
            return new Outcome(JobRunStatus.Lost, null,
                "no container for this run was found when re-attaching after a gateway restart; the outcome is unknown");
        }

        Job? job;
        using (var scope = _scopeFactory.CreateScope())
        {
            job = await scope.ServiceProvider.GetRequiredService<IJobStore>().GetAsync(run.Job, CancellationToken.None);
        }

        var timeoutSeconds = job?.TimeoutSeconds ?? JobEndpoints.DefaultTimeoutSeconds;
        var startedAt = container.StartedAt ?? run.StartedAt ?? _time.GetUtcNow();
        var remaining = TimeSpan.FromSeconds(timeoutSeconds) - (_time.GetUtcNow() - startedAt);
        if (remaining < TimeSpan.FromSeconds(1))
        {
            remaining = TimeSpan.FromSeconds(1);
        }

        _logger.LogInformation(
            "Re-attached to container {Container} of run {RunId} (job {Job}); waiting up to {Remaining}.",
            container.Name, run.Id, run.Job, remaining);

        var result = await _runtime.WaitJobContainerAsync(container, remaining, cancel);
        if (job is not null)
        {
            await EnsureRetentionAsync(job.Name);
        }

        return Classify(result, timeoutSeconds);
    }

    private static Outcome CancelledBeforeStart() =>
        new(JobRunStatus.Cancelled, null, "cancelled before the container started");

    private static Outcome Classify(JobRunResult result, int timeoutSeconds)
    {
        if (result.Cancelled)
        {
            return new Outcome(JobRunStatus.Cancelled, result.ExitCode, WithNote(result.Tail, "cancelled by request; container killed"));
        }

        if (result.TimedOut)
        {
            return new Outcome(JobRunStatus.TimedOut, result.ExitCode, WithNote(result.Tail,
                string.Create(CultureInfo.InvariantCulture, $"timed out after {timeoutSeconds}s; container killed")));
        }

        if (result.ExitCode == 0)
        {
            return new Outcome(JobRunStatus.Succeeded, 0, string.IsNullOrEmpty(result.Tail) ? null : result.Tail);
        }

        return new Outcome(JobRunStatus.Failed, result.ExitCode, string.IsNullOrEmpty(result.Tail)
            ? (result.ExitCode is { } code
                ? string.Create(CultureInfo.InvariantCulture, $"container exited with code {code}")
                : "container exited without an exit code")
            : result.Tail);
    }

    // The note goes last so a truncation from the front keeps it.
    private static string WithNote(string tail, string note) =>
        string.IsNullOrEmpty(tail) ? note : tail + "\n" + note;

    /// <summary>
    /// The detail for a failed secret read. It names the ref; a
    /// <see cref="SecretResolutionException"/> message is built never to echo a value,
    /// and any other exception contributes only its type name.
    /// </summary>
    private static string SecretFailureDetail(string? reference, Exception ex) =>
        ex is SecretResolutionException
            ? $"env secret '{reference}' could not be read: {ex.Message}"
            : $"env secret '{reference}' could not be read ({ex.GetType().Name})";

    /// <summary>Keep the end of <paramref name="detail"/> within <see cref="MaxDetailBytes"/> UTF-8 bytes.</summary>
    public static string? TruncateDetail(string? detail)
    {
        if (detail is null || Encoding.UTF8.GetByteCount(detail) <= MaxDetailBytes)
        {
            return detail;
        }

        var start = Math.Max(0, detail.Length - MaxDetailBytes);
        while (Encoding.UTF8.GetByteCount(detail.AsSpan(start)) > MaxDetailBytes)
        {
            start++;
        }

        // Never start on the low half of a surrogate pair.
        if (start < detail.Length && char.IsLowSurrogate(detail[start]))
        {
            start++;
        }

        return detail[start..];
    }

    private async Task FinishAsync(string self, JobRun run, Outcome outcome)
    {
        var finishedAt = _time.GetUtcNow();
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IJobStore>();

        var changed = await store.FinishRunAsync(
            run.Id, self, outcome.Status, outcome.ExitCode, TruncateDetail(outcome.Detail), finishedAt, CancellationToken.None);
        if (!changed)
        {
            _logger.LogInformation("Run {RunId} of job {Job} was already finalised elsewhere.", run.Id, run.Job);
            return;
        }

        var after = await store.GetRunAsync(run.Id, CancellationToken.None);
        var startedAt = after?.StartedAt ?? run.StartedAt;
        _metrics.RunFinished(run.Job, outcome.Status, startedAt is { } s ? finishedAt - s : null);
        _logger.LogInformation(
            "Run {RunId} of job {Job} finished {Status} (exit code {ExitCode}).", run.Id, run.Job, outcome.Status, outcome.ExitCode);
        Broadcast(after);
    }

    private void Broadcast(JobRun? run)
    {
        if (run is not null && _publisher is not null)
        {
            JobEndpoints.BroadcastRun(_publisher, run);
        }
    }

    /// <summary>
    /// Set 30-day retention on a job's log group the first time this process uses it,
    /// the same way service groups get theirs. Only for the awslogs driver; a failure
    /// forgets the mark so the next run retries.
    /// </summary>
    private async Task EnsureRetentionAsync(string job)
    {
        if (!LogConfigFactory.UsesAwsLogs(_reconciler.LogDriver))
        {
            return;
        }

        var group = CloudWatchLogStore.JobLogGroupFor(job);
        lock (_retentionEnsured)
        {
            if (!_retentionEnsured.Add(group))
            {
                return;
            }
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var admin = scope.ServiceProvider.GetService<ILogGroupAdmin>();
            if (admin is not null)
            {
                await admin.EnsureRetentionAsync(group, LogConfigFactory.RetentionDays, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set retention on log group {Group}; the next run retries.", group);
            lock (_retentionEnsured)
            {
                _retentionEnsured.Remove(group);
            }
        }
    }
}
