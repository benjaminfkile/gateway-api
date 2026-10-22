using System.Collections.Concurrent;
using System.Globalization;
using Gateway.Api.Containers;

namespace Gateway.Api.Tests;

/// <summary>
/// The scripted job-run side of <see cref="FakeContainerRuntime"/>: each
/// <see cref="RunJobContainerAsync"/> call takes a <see cref="JobScript"/> from
/// <see cref="JobScripts"/> (exit code, delay, output, hang, time out, throw) and
/// honours the timeout and the cancel token for real. Every spec is checked for the
/// job contract: job labels only (never the managed-service label), a memory limit,
/// and no port (the spec type has none, and the fake records zero published ports).
/// </summary>
public sealed partial class FakeContainerRuntime
{
    /// <summary>How a scripted job container behaves.</summary>
    public sealed class JobScript
    {
        /// <summary>Exit code the container reports when it exits by itself.</summary>
        public int ExitCode { get; init; }

        /// <summary>How long the container runs before exiting by itself.</summary>
        public TimeSpan Delay { get; init; } = TimeSpan.Zero;

        /// <summary>Never exits by itself: only a kill (timeout, cancel) or <see cref="ExitJobContainer"/> ends it.</summary>
        public bool Hang { get; init; }

        /// <summary>Reports a timeout at once, as if the timeout elapsed and the container was killed.</summary>
        public bool TimesOut { get; init; }

        /// <summary>Output lines the container writes; the result's tail is the last 20.</summary>
        public string Output { get; init; } = string.Empty;

        /// <summary>When set, the run call throws this instead of creating a container.</summary>
        public Exception? Throw { get; init; }
    }

    /// <summary>A job container on the fake daemon.</summary>
    public sealed class FakeJobContainer
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Image { get; init; }
        public required IReadOnlyDictionary<string, string> Labels { get; init; }
        public required string Output { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public int PublishedPorts { get; init; }
        public long MemoryLimitBytes { get; init; }
        public TaskCompletionSource<int> Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string State => Exit.Task.IsCompleted ? "exited" : "running";
    }

    private readonly Dictionary<string, FakeJobContainer> _jobContainers = new(StringComparer.Ordinal);
    private TaskCompletionSource _abandon = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _runningJobs;
    private int _maxRunningJobs;
    private int _nextJobContainerId = 1;

    /// <summary>Picks the script for each run call; defaults to exit 0 at once.</summary>
    public Func<JobContainerSpec, JobScript> JobScripts { get; set; } = _ => new JobScript();

    /// <summary>Specs passed to <see cref="RunJobContainerAsync"/>, in order.</summary>
    public ConcurrentQueue<JobContainerSpec> JobSpecs { get; } = new();

    /// <summary>Timeouts passed to run and wait calls, in order.</summary>
    public ConcurrentQueue<TimeSpan> JobTimeouts { get; } = new();

    /// <summary>Job containers waited on right now.</summary>
    public int RunningJobContainers => Volatile.Read(ref _runningJobs);

    /// <summary>The most job containers waited on at the same time.</summary>
    public int MaxRunningJobContainers => Volatile.Read(ref _maxRunningJobs);

    /// <summary>Snapshot of a job container by name, or null.</summary>
    public FakeJobContainer? GetJobContainer(string name)
    {
        lock (_gate)
        {
            return _jobContainers.TryGetValue(name, out var c) ? c : null;
        }
    }

    /// <summary>Make a running job container exit with <paramref name="exitCode"/>.</summary>
    public void ExitJobContainer(string name, int exitCode)
    {
        GetJobContainer(name)?.Exit.TrySetResult(exitCode);
    }

    /// <summary>
    /// Seed a job container left behind by an earlier gateway process: running, or
    /// already exited when <paramref name="exitCode"/> is given.
    /// </summary>
    public FakeJobContainer SeedJobContainer(
        string job, long runId, int? exitCode = null, string output = "", bool alsoManagedLabel = false)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ContainerLabels.Job] = job,
            [ContainerLabels.JobRun] = runId.ToString(CultureInfo.InvariantCulture),
        };
        if (alsoManagedLabel)
        {
            labels[ContainerLabels.Managed] = "true";
        }

        var container = AddJobContainer($"job-{job}-{runId}", "gateway-job-runner", labels, output, 0, 0);
        if (exitCode is { } code)
        {
            container.Exit.TrySetResult(code);
        }

        return container;
    }

    /// <summary>
    /// Model the gateway process dying: every run or wait call in flight never
    /// returns (its process is gone), while the containers keep their state so a new
    /// scheduler can re-attach to them.
    /// </summary>
    public void SimulateGatewayRestart()
    {
        TaskCompletionSource old;
        lock (_gate)
        {
            old = _abandon;
            _abandon = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        old.TrySetResult();
    }

    public async Task<JobRunResult> RunJobContainerAsync(JobContainerSpec spec, TimeSpan timeout, CancellationToken cancel)
    {
        AssertJobContract(spec);
        JobSpecs.Enqueue(spec);
        var script = JobScripts(spec);
        if (script.Throw is { } toThrow)
        {
            Operations.Enqueue($"RunJobFailed:{spec.Name}");
            throw toThrow;
        }

        var container = AddJobContainer(
            spec.Name, spec.Image, spec.Labels, script.Output, publishedPorts: 0, spec.MemoryLimitBytes);
        Operations.Enqueue($"RunJob:{spec.Name}");

        if (!script.Hang && !script.TimesOut)
        {
            _ = Task.Delay(script.Delay).ContinueWith(_ => container.Exit.TrySetResult(script.ExitCode), TaskScheduler.Default);
        }

        return await WaitCoreAsync(container, timeout, cancel, forceTimeout: script.TimesOut);
    }

    public Task<JobContainerInfo?> FindJobContainerAsync(long runId, CancellationToken ct = default)
    {
        var id = runId.ToString(CultureInfo.InvariantCulture);
        lock (_gate)
        {
            var c = _jobContainers.Values.FirstOrDefault(j =>
                j.Labels.TryGetValue(ContainerLabels.JobRun, out var label) && label == id);
            return Task.FromResult(c is null
                ? null
                : new JobContainerInfo(c.Id, c.Name, c.Labels[ContainerLabels.Job], runId, c.State, c.StartedAt));
        }
    }

    public async Task<JobRunResult> WaitJobContainerAsync(JobContainerInfo container, TimeSpan timeout, CancellationToken cancel)
    {
        var c = GetJobContainer(container.Name)
            ?? throw new InvalidOperationException($"No such container {container.Name}");
        Operations.Enqueue($"AttachJob:{c.Name}");
        return await WaitCoreAsync(c, timeout, cancel, forceTimeout: false);
    }

    private FakeJobContainer AddJobContainer(
        string name, string image, IReadOnlyDictionary<string, string> labels, string output, int publishedPorts, long memory)
    {
        lock (_gate)
        {
            var container = new FakeJobContainer
            {
                Id = $"fake-{_nextJobContainerId++}",
                Name = name,
                Image = image,
                Labels = new Dictionary<string, string>(labels, StringComparer.Ordinal),
                Output = output,
                StartedAt = NewContainerStartedAt,
                PublishedPorts = publishedPorts,
                MemoryLimitBytes = memory,
            };
            _jobContainers[name] = container;
            return container;
        }
    }

    private async Task<JobRunResult> WaitCoreAsync(
        FakeJobContainer container, TimeSpan timeout, CancellationToken cancel, bool forceTimeout)
    {
        JobTimeouts.Enqueue(timeout);
        Task abandoned;
        lock (_gate)
        {
            abandoned = _abandon.Task;
        }

        var running = Interlocked.Increment(ref _runningJobs);
        InterlockedMax(ref _maxRunningJobs, running);
        try
        {
            int? exitCode = null;
            var timedOut = forceTimeout;
            var cancelled = false;
            if (!forceTimeout)
            {
                using var stop = new CancellationTokenSource();
                var timeoutTask = Task.Delay(timeout, stop.Token);
                var cancelTask = Task.Delay(Timeout.Infinite, cancel);
                var winner = await Task.WhenAny(container.Exit.Task, timeoutTask, cancelTask, abandoned);
                stop.Cancel();

                if (winner == abandoned)
                {
                    // The process that made this call is gone: it never returns.
                    Interlocked.Decrement(ref _runningJobs);
                    await new TaskCompletionSource().Task;
                }

                if (winner == container.Exit.Task)
                {
                    exitCode = container.Exit.Task.Result;
                }
                else
                {
                    cancelled = winner == cancelTask;
                    timedOut = !cancelled;
                }
            }

            if (exitCode is null)
            {
                container.Exit.TrySetResult(137);
                Operations.Enqueue($"KillJob:{container.Name}");
            }

            var lines = container.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var tail = string.Join('\n', lines.Skip(Math.Max(0, lines.Length - JobRunResult.TailLines)));
            return new JobRunResult(exitCode, timedOut, cancelled, container.StartedAt, DateTimeOffset.UtcNow, tail);
        }
        finally
        {
            if (!abandoned.IsCompleted)
            {
                Interlocked.Decrement(ref _runningJobs);
                lock (_gate)
                {
                    _jobContainers.Remove(container.Name);
                }

                Operations.Enqueue($"RemoveJob:{container.Name}");
            }
        }
    }

    private static void AssertJobContract(JobContainerSpec spec)
    {
        if (!spec.Labels.ContainsKey(ContainerLabels.Job) || !spec.Labels.ContainsKey(ContainerLabels.JobRun))
        {
            throw new InvalidOperationException($"Job container {spec.Name} is missing the job labels.");
        }

        if (spec.Labels.Keys.Any(k => k is not ContainerLabels.Job and not ContainerLabels.JobRun))
        {
            throw new InvalidOperationException(
                $"Job container {spec.Name} carries labels beyond the job labels: {string.Join(", ", spec.Labels.Keys)}.");
        }

        if (spec.MemoryLimitBytes <= 0)
        {
            throw new InvalidOperationException($"Job container {spec.Name} has no memory limit.");
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value)
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }
}
