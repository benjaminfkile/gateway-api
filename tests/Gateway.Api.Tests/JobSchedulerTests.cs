using System.Text;
using Gateway.Api.Containers;
using Gateway.Api.Data;
using Gateway.Api.Instances;
using Gateway.Api.Jobs;
using Gateway.Api.Management;
using Gateway.Api.Reconcile;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Api.Tests;

/// <summary>
/// The job executor (docs/jobs.md section 3) against fakes: <see cref="FakeJobStore"/>
/// for the ledger, <see cref="FakeContainerRuntime"/> for Docker,
/// <see cref="FakeSecretStore"/> behind the real <see cref="SecretsManagerEnvProvider"/>,
/// and a stub <see cref="ILeaderStatus"/>. Covers every row of the section 9 failure
/// table plus claim races, catch-up, cancel, timeout, re-attach and concurrency.
/// </summary>
public class JobSchedulerTests
{
    private const string InstanceA = "i-0000000000000000a";
    private const string InstanceB = "i-0000000000000000b";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class StubLeaderStatus : ILeaderStatus
    {
        public bool IsLeader { get; set; } = true;
        public DateTimeOffset? At { get; set; }

        public LeaderResolution Current => new(IsLeader, IsLeader ? InstanceA : null);
        public DateTimeOffset? EvaluatedAt => At;
    }

    private sealed class FakeJobMetrics : IJobMetrics
    {
        private readonly object _gate = new();
        private readonly List<(string Job, string Status, TimeSpan? Duration)> _calls = new();

        public IReadOnlyList<(string Job, string Status, TimeSpan? Duration)> Calls
        {
            get
            {
                lock (_gate)
                {
                    return _calls.ToList();
                }
            }
        }

        public void RunFinished(string job, string status, TimeSpan? duration)
        {
            lock (_gate)
            {
                _calls.Add((job, status, duration));
            }
        }
    }

    private sealed class Harness
    {
        public FakeJobStore Store { get; } = new();
        public FakeContainerRuntime Runtime { get; } = new();
        public FakeInstanceStatusStore Status { get; } = new();
        public FakeLogGroupAdmin LogAdmin { get; } = new();
        public FakeSecretStore Secrets { get; } = new();
        public FakeJobMetrics Metrics { get; } = new();
        public JobTestHost.RecordingPublisher Events { get; } = new();
        public ManualTimeProvider Clock { get; } = new() { Now = new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero) };
        public ReconcilerOptions Reconciler { get; } = new() { Network = "app-net" };
        public SecretsManagerEnvProvider Env { get; }
        private readonly ServiceProvider _provider;

        public Harness()
        {
            Env = new SecretsManagerEnvProvider(Secrets, TimeSpan.Zero, Clock);
            var services = new ServiceCollection();
            services.AddSingleton<IJobStore>(Store);
            services.AddSingleton<IInstanceStatusStore>(Status);
            services.AddSingleton<ILogGroupAdmin>(LogAdmin);
            _provider = services.BuildServiceProvider();
        }

        public (JobSchedulerService Scheduler, StubLeaderStatus Leader) Scheduler(
            string instanceId = InstanceA, bool leader = true, int maxConcurrent = 4)
        {
            var status = new StubLeaderStatus { IsLeader = leader, At = Clock.Now };
            var metadata = new InstanceMetadataProvider(new IInstanceMetadata[]
            {
                new StubInstanceMetadata(new InstanceIdentity(instanceId, null, null)),
            });
            var scheduler = new JobSchedulerService(
                Runtime,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                Env,
                status,
                metadata,
                new JobSchedulerOptions { MaxConcurrent = maxConcurrent, TickInterval = TimeSpan.FromMilliseconds(20) },
                Reconciler,
                Metrics,
                NullLogger<JobSchedulerService>.Instance,
                Clock,
                migrationGate: null,
                publisher: Events);
            return (scheduler, status);
        }

        public Job AddJob(
            string name,
            string script = "echo hi",
            string? schedule = null,
            DateTimeOffset? nextRunAt = null,
            string? envSecretRef = null,
            int timeoutSeconds = 3600)
        {
            var job = JobTestHost.Job(name, script, schedule, enabled: true);
            job.NextRunAt = nextRunAt;
            job.EnvSecretRef = envSecretRef;
            job.TimeoutSeconds = timeoutSeconds;
            Store.Jobs.Add(job);
            return job;
        }

        public async Task<long> EnqueueManualAsync(string job)
        {
            var run = await Store.TryEnqueueRunAsync(new JobRun
            {
                Job = job,
                Trigger = JobRunTrigger.Manual,
                Actor = "alice-admin",
                Status = JobRunStatus.Queued,
                ScriptVersion = 1,
                QueuedAt = Clock.Now,
            });
            return run!.Id;
        }

        public void Heartbeat(string instanceId, DateTimeOffset at) =>
            Status.Seed(new InstanceStatus { InstanceId = instanceId, HeartbeatAt = at });
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }

            await Task.Delay(10);
        }
    }

    private static async Task TickAndDrainAsync(JobSchedulerService scheduler)
    {
        await scheduler.RunOnceAsync();
        await scheduler.WaitForDriversAsync().WaitAsync(Wait);
    }

    // ---------- happy path, container contract, logs, metrics, events ----------

    [Fact]
    public async Task Due_scheduled_run_is_claimed_driven_and_succeeds()
    {
        var h = new Harness();
        h.AddJob("nightly", schedule: "0 4 * * *", nextRunAt: h.Clock.Now.AddMinutes(-1));
        h.Runtime.JobScripts = _ => new FakeContainerRuntime.JobScript { ExitCode = 0, Output = "step 1\nstep 2\ndone\n" };
        var (scheduler, _) = h.Scheduler();

        await TickAndDrainAsync(scheduler);

        var run = Assert.Single(h.Store.AllRuns());
        Assert.Equal(JobRunStatus.Succeeded, run.Status);
        Assert.Equal(JobRunTrigger.Schedule, run.Trigger);
        Assert.Equal(JobSchedulerService.ScheduleActor, run.Actor);
        Assert.Equal(InstanceA, run.InstanceId);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("sha256:gateway-job-runner-latest", run.ImageDigest);
        Assert.Equal("step 1\nstep 2\ndone", run.Detail);
        Assert.Equal(h.Clock.Now, run.StartedAt);
        Assert.NotNull(run.FinishedAt);

        // next_run_at advanced to the next 04:00 UTC after now.
        Assert.Equal(new DateTimeOffset(2026, 1, 6, 4, 0, 0, TimeSpan.Zero), h.Store.JobRow("nightly").NextRunAt);

        // Status events on ops:jobs: queued, running, succeeded.
        Assert.Equal(
            new[] { JobRunStatus.Queued, JobRunStatus.Running, JobRunStatus.Succeeded },
            h.Events.OnJobs("run").Select(e => (string)e.Data.GetType().GetProperty("status")!.GetValue(e.Data)!).ToArray());

        Assert.Equal(("nightly", JobRunStatus.Succeeded), (h.Metrics.Calls.Single().Job, h.Metrics.Calls.Single().Status));
        Assert.False(h.Runtime.Exists("job-nightly-1"));
        Assert.Null(h.Runtime.GetJobContainer("job-nightly-1"));
    }

    [Fact]
    public async Task Job_container_spec_has_job_labels_memory_limit_network_and_job_log_group()
    {
        var h = new Harness();
        h.AddJob("mirror");
        var id = await h.EnqueueManualAsync("mirror");
        var (scheduler, _) = h.Scheduler();

        await TickAndDrainAsync(scheduler);

        var spec = Assert.Single(h.Runtime.JobSpecs);
        Assert.Equal($"job-mirror-{id}", spec.Name);
        Assert.Equal("gateway-job-runner", spec.Image);
        Assert.Equal("sha256:gateway-job-runner-latest", spec.Digest);
        Assert.Equal("app-net", spec.Network);
        Assert.Equal(1L << 30, spec.MemoryLimitBytes);
        Assert.Equal(
            new Dictionary<string, string> { [ContainerLabels.Job] = "mirror", [ContainerLabels.JobRun] = id.ToString() },
            spec.Labels);
        Assert.DoesNotContain(ContainerLabels.Managed, spec.Labels.Keys);

        Assert.Equal("awslogs", spec.LogConfig.Driver);
        Assert.Equal("/gateway/jobs/mirror", spec.LogConfig.Options["awslogs-group"]);
        Assert.Equal($"run-{id}", spec.LogConfig.Options["awslogs-stream"]);
        Assert.Equal("true", spec.LogConfig.Options["awslogs-create-group"]);

        // Retention ensured once for the job's group, 30 days, like service groups.
        Assert.Equal(new[] { ("/gateway/jobs/mirror", 30) }, h.LogAdmin.Calls);
        Assert.Equal(TimeSpan.FromSeconds(3600), Assert.Single(h.Runtime.JobTimeouts));
    }

    [Fact]
    public async Task Retention_is_ensured_only_the_first_time_a_job_group_is_used()
    {
        var h = new Harness();
        h.AddJob("mirror");
        var (scheduler, _) = h.Scheduler();

        await h.EnqueueManualAsync("mirror");
        await TickAndDrainAsync(scheduler);
        await h.EnqueueManualAsync("mirror");
        await TickAndDrainAsync(scheduler);

        Assert.Equal(2, h.Store.AllRuns().Count(r => r.Status == JobRunStatus.Succeeded));
        Assert.Single(h.LogAdmin.Calls);
    }

    [Fact]
    public async Task Env_hands_over_secret_values_and_the_four_gateway_job_variables_with_the_script_base64()
    {
        var h = new Harness();
        const string script = "#!/bin/bash\necho \"café ✓\"\nexit 0\n";
        h.Secrets.Secrets["gateway/jobs/mirror"] = "{\"DEV_API_URL\":\"dev-api-url-placeholder\",\"GATEWAY_JOB_NAME\":\"spoofed\"}";
        h.AddJob("mirror", script: script, envSecretRef: "gateway/jobs/mirror");
        var id = await h.EnqueueManualAsync("mirror");
        var (scheduler, _) = h.Scheduler();

        await TickAndDrainAsync(scheduler);

        var env = Assert.Single(h.Runtime.JobSpecs).EnvVars;
        Assert.Equal("dev-api-url-placeholder", env["DEV_API_URL"]);
        Assert.Equal("mirror", env[JobEnv.Name]);
        Assert.Equal(id.ToString(), env[JobEnv.RunId]);
        Assert.Equal(JobRunTrigger.Manual, env[JobEnv.Trigger]);
        Assert.Equal(script, Encoding.UTF8.GetString(Convert.FromBase64String(env[JobEnv.Script])));
        Assert.Equal(5, env.Count);
    }

    // ---------- section 9 failure table ----------

    [Fact]
    public async Task Secret_missing_fails_the_run_before_start_naming_the_ref()
    {
        var h = new Harness();
        h.AddJob("mirror", envSecretRef: "gateway/jobs/absent");
        await h.EnqueueManualAsync("mirror");
        var (scheduler, _) = h.Scheduler();

        await TickAndDrainAsync(scheduler);

        var run = Assert.Single(h.Store.AllRuns());
        Assert.Equal(JobRunStatus.Failed, run.Status);
        Assert.Contains("gateway/jobs/absent", run.Detail);
        Assert.Null(run.ExitCode);
        Assert.Empty(h.Runtime.JobSpecs);
        Assert.DoesNotContain(h.Runtime.Operations, op => op.StartsWith("Pull:", StringComparison.Ordinal));
        Assert.Equal(JobRunStatus.Failed, h.Metrics.Calls.Single().Status);
    }

    [Fact]
    public async Task Secret_not_flat_json_fails_the_run_naming_the_ref_never_a_value()
    {
        var h = new Harness();
        h.Secrets.Secrets["gateway/jobs/nested"] = "{\"DB\":{\"password\":\"placeholder-secret-value\"}}";
        h.AddJob("mirror", envSecretRef: "gateway/jobs/nested");
        await h.EnqueueManualAsync("mirror");
        var (scheduler, _) = h.Scheduler();

        await TickAndDrainAsync(scheduler);

        var run = Assert.Single(h.Store.AllRuns());
        Assert.Equal(JobRunStatus.Failed, run.Status);
        Assert.Contains("gateway/jobs/nested", run.Detail);
        Assert.DoesNotContain("placeholder-secret-value", run.Detail);
        Assert.Empty(h.Runtime.JobSpecs);
    }

    [Fact]
    public async Task Image_pull_failure_fails_the_run_with_the_registry_error()
    {
        var h = new Harness();
        h.Runtime.PullDigest = (_, _) => throw new InvalidOperationException("manifest unknown: tag does-not-exist");
        h.AddJob("mirror");
        await h.EnqueueManualAsync("mirror");
        var (scheduler, _) = h.Scheduler();

        await TickAndDrainAsync(scheduler);

        var run = Assert.Single(h.Store.AllRuns());
        Assert.Equal(JobRunStatus.Failed, run.Status);
        Assert.Contains("manifest unknown: tag does-not-exist", run.Detail);
        Assert.Null(run.ImageDigest);
        Assert.Empty(h.Runtime.JobSpecs);
    }

    [Fact]
    public async Task Script_exiting_non_zero_fails_with_exit_code_and_the_last_twenty_lines()
    {
        var h = new Harness();
        var output = string.Join('\n', Enumerable.Range(1, 25).Select(i => $"line {i}"));
        h.Runtime.JobScripts = _ => new FakeContainerRuntime.JobScript { ExitCode = 3, Output = output };
        h.AddJob("mirror");
        await h.EnqueueManualAsync("mirror");
        var (scheduler, _) = h.Scheduler();

        await TickAndDrainAsync(scheduler);

        var run = Assert.Single(h.Store.AllRuns());
        Assert.Equal(JobRunStatus.Failed, run.Status);
        Assert.Equal(3, run.ExitCode);
        var lines = run.Detail!.Split('\n');
        Assert.Equal(20, lines.Length);
        Assert.Equal("line 6", lines[0]);
        Assert.Equal("line 25", lines[^1]);
    }

    [Fact]
    public async Task Timeout_kills_the_container_and_finalises_as_timed_out()
    {
        var h = new Harness();
        h.Runtime.JobScripts = _ => new FakeContainerRuntime.JobScript { Hang = true, Output = "working\n" };
        h.AddJob("slow", timeoutSeconds: 1);
        var id = await h.EnqueueManualAsync("slow");
        var (scheduler, _) = h.Scheduler();

        await TickAndDrainAsync(scheduler);

        var run = h.Store.Run(id);
        Assert.Equal(JobRunStatus.TimedOut, run.Status);
        Assert.Null(run.ExitCode);
        Assert.Equal("working\ntimed out after 1s; container killed", run.Detail);
        Assert.Contains($"KillJob:job-slow-{id}", h.Runtime.Operations);
        Assert.Contains($"RemoveJob:job-slow-{id}", h.Runtime.Operations);
        Assert.Equal(JobRunStatus.TimedOut, h.Metrics.Calls.Single().Status);
    }

    [Fact]
    public async Task Scripted_timeout_behaviour_finalises_as_timed_out()
    {
        var h = new Harness();
        h.Runtime.JobScripts = _ => new FakeContainerRuntime.JobScript { TimesOut = true };
        h.AddJob("slow");
        var id = await h.EnqueueManualAsync("slow");
        var (scheduler, _) = h.Scheduler();

        await TickAndDrainAsync(scheduler);

        Assert.Equal(JobRunStatus.TimedOut, h.Store.Run(id).Status);
    }

    [Fact]
    public async Task Cancel_of_a_running_run_kills_the_container_and_finalises_as_cancelled()
    {
        var h = new Harness();
        h.Runtime.JobScripts = _ => new FakeContainerRuntime.JobScript { Hang = true };
        h.AddJob("mirror");
        var id = await h.EnqueueManualAsync("mirror");
        var (scheduler, _) = h.Scheduler();

        await scheduler.RunOnceAsync();
        await Eventually(() => h.Runtime.RunningJobContainers == 1, "the container to run");
        Assert.Equal(JobRunStatus.Running, h.Store.Run(id).Status);

        // What POST .../cancel does: stamp cancel_requested_at on the running run.
        await h.Store.CancelRunAsync(id, h.Clock.Now);
        await TickAndDrainAsync(scheduler);

        var run = h.Store.Run(id);
        Assert.Equal(JobRunStatus.Cancelled, run.Status);
        Assert.Contains("cancelled by request", run.Detail);
        Assert.Contains($"KillJob:job-mirror-{id}", h.Runtime.Operations);
        Assert.Empty(scheduler.DrivenRunIds);
    }

    [Fact]
    public async Task Leader_marks_a_run_lost_when_its_instance_heartbeat_is_stale()
    {
        var h = new Harness();
        h.AddJob("gone");
        h.AddJob("alive");
        h.Store.Runs.Add(JobTestHost.Run(10, "gone", JobRunStatus.Running, InstanceB));
        h.Store.Runs.Add(JobTestHost.Run(11, "alive", JobRunStatus.Running, "i-0000000000000000c"));
        h.Heartbeat(InstanceA, h.Clock.Now);
        h.Heartbeat(InstanceB, h.Clock.Now.AddMinutes(-5));
        h.Heartbeat("i-0000000000000000c", h.Clock.Now.AddSeconds(-20));
        var (scheduler, _) = h.Scheduler();

        await TickAndDrainAsync(scheduler);

        var lost = h.Store.Run(10);
        Assert.Equal(JobRunStatus.Lost, lost.Status);
        Assert.Contains(InstanceB, lost.Detail);
        Assert.Equal(h.Clock.Now, lost.FinishedAt);
        Assert.Equal(JobRunStatus.Running, h.Store.Run(11).Status);
        Assert.Equal(("gone", JobRunStatus.Lost), (h.Metrics.Calls.Single().Job, h.Metrics.Calls.Single().Status));
    }

    [Fact]
    public async Task A_run_whose_instance_has_no_status_row_is_lost_and_a_non_leader_never_marks_lost()
    {
        var h = new Harness();
        h.AddJob("gone");
        h.Store.Runs.Add(JobTestHost.Run(10, "gone", JobRunStatus.Running, InstanceB));
        var (follower, _) = h.Scheduler(InstanceA, leader: false);

        await TickAndDrainAsync(follower);
        Assert.Equal(JobRunStatus.Running, h.Store.Run(10).Status);

        var (leader, _) = h.Scheduler(InstanceA, leader: true);
        await TickAndDrainAsync(leader);
        Assert.Equal(JobRunStatus.Lost, h.Store.Run(10).Status);
    }

    [Fact]
    public async Task Leader_dies_mid_run_and_the_runs_own_instance_still_finishes_it()
    {
        var h = new Harness();
        h.Runtime.JobScripts = _ => new FakeContainerRuntime.JobScript { Hang = true };
        h.AddJob("mirror");
        var id = await h.EnqueueManualAsync("mirror");
        var (b, bLeader) = h.Scheduler(InstanceB, leader: true);

        await b.RunOnceAsync();
        await Eventually(() => h.Runtime.RunningJobContainers == 1, "the container to run");

        // Leadership moves to A; B keeps heartbeating and keeps driving its run.
        bLeader.IsLeader = false;
        h.Heartbeat(InstanceA, h.Clock.Now);
        h.Heartbeat(InstanceB, h.Clock.Now);
        var (a, _) = h.Scheduler(InstanceA, leader: true);
        await a.RunOnceAsync();
        await b.RunOnceAsync();
        Assert.Equal(JobRunStatus.Running, h.Store.Run(id).Status);

        h.Runtime.ExitJobContainer($"job-mirror-{id}", 0);
        await b.WaitForDriversAsync().WaitAsync(Wait);

        var run = h.Store.Run(id);
        Assert.Equal(JobRunStatus.Succeeded, run.Status);
        Assert.Equal(InstanceB, run.InstanceId);
    }

    [Fact]
    public async Task Gateway_restart_mid_run_re_attaches_by_label_and_finalises_with_the_real_exit_code()
    {
        var h = new Harness();
        h.Runtime.JobScripts = _ => new FakeContainerRuntime.JobScript { Hang = true, Output = "half way\n" };
        h.AddJob("mirror");
        var id = await h.EnqueueManualAsync("mirror");
        var (first, _) = h.Scheduler(InstanceA);

        await first.RunOnceAsync();
        await Eventually(() => h.Runtime.RunningJobContainers == 1, "the container to run");

        // The gateway process dies; the container keeps running under Docker.
        h.Runtime.SimulateGatewayRestart();
        Assert.Equal(JobRunStatus.Running, h.Store.Run(id).Status);
        Assert.NotNull(h.Runtime.GetJobContainer($"job-mirror-{id}"));

        var (second, _) = h.Scheduler(InstanceA);
        await second.RunOnceAsync();
        await Eventually(() => h.Runtime.Operations.Contains($"AttachJob:job-mirror-{id}"), "the re-attach");
        Assert.Single(h.Runtime.JobSpecs);

        h.Runtime.ExitJobContainer($"job-mirror-{id}", 7);
        await second.WaitForDriversAsync().WaitAsync(Wait);

        var run = h.Store.Run(id);
        Assert.Equal(JobRunStatus.Failed, run.Status);
        Assert.Equal(7, run.ExitCode);
        Assert.Equal("half way", run.Detail);
        Assert.Null(h.Runtime.GetJobContainer($"job-mirror-{id}"));
    }

    [Fact]
    public async Task Re_attach_to_a_container_that_exited_while_the_gateway_was_down_uses_its_exit_code()
    {
        var h = new Harness();
        h.AddJob("mirror");
        h.Store.Runs.Add(JobTestHost.Run(42, "mirror", JobRunStatus.Running, InstanceA));
        h.Runtime.SeedJobContainer("mirror", 42, exitCode: 0, output: "all done\n");
        var (scheduler, _) = h.Scheduler(InstanceA, leader: false);

        await TickAndDrainAsync(scheduler);

        var run = h.Store.Run(42);
        Assert.Equal(JobRunStatus.Succeeded, run.Status);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("all done", run.Detail);
        Assert.Empty(h.Runtime.JobSpecs);
    }

    [Fact]
    public async Task Re_attach_with_no_container_marks_the_run_lost()
    {
        var h = new Harness();
        h.AddJob("mirror");
        h.Store.Runs.Add(JobTestHost.Run(42, "mirror", JobRunStatus.Running, InstanceA));
        var (scheduler, _) = h.Scheduler(InstanceA, leader: false);

        await TickAndDrainAsync(scheduler);

        var run = h.Store.Run(42);
        Assert.Equal(JobRunStatus.Lost, run.Status);
        Assert.Contains("re-attaching", run.Detail);
        Assert.Empty(h.Runtime.JobSpecs);
    }

    [Fact]
    public async Task Two_leaders_ticking_together_claim_a_due_run_and_a_queued_run_once()
    {
        var h = new Harness();
        h.Runtime.JobScripts = _ => new FakeContainerRuntime.JobScript { Hang = true };
        h.AddJob("scheduled", schedule: "*/5 * * * *", nextRunAt: h.Clock.Now.AddSeconds(-1));
        h.AddJob("manual");
        await h.EnqueueManualAsync("manual");
        h.Heartbeat(InstanceA, h.Clock.Now);
        h.Heartbeat(InstanceB, h.Clock.Now);
        var (a, _) = h.Scheduler(InstanceA);
        var (b, _) = h.Scheduler(InstanceB);

        for (var i = 0; i < 5; i++)
        {
            await Task.WhenAll(Task.Run(() => a.RunOnceAsync()), Task.Run(() => b.RunOnceAsync()));
        }

        await Eventually(() => h.Runtime.RunningJobContainers == 2, "both runs to start");
        var runs = h.Store.AllRuns();
        Assert.Equal(2, runs.Count);
        Assert.Single(runs, r => r.Job == "scheduled");
        Assert.All(runs, r => Assert.Equal(JobRunStatus.Running, r.Status));
        Assert.Equal(2, h.Runtime.JobSpecs.Count);
        Assert.Equal(2, a.DrivenRunIds.Count + b.DrivenRunIds.Count);
        Assert.Empty(a.DrivenRunIds.Intersect(b.DrivenRunIds));
    }

    [Fact]
    public async Task A_job_due_for_many_missed_slots_runs_once_and_is_rescheduled_after_now()
    {
        var h = new Harness();
        h.AddJob("hourly", schedule: "0 * * * *", nextRunAt: h.Clock.Now.AddDays(-3));
        var (scheduler, _) = h.Scheduler();

        for (var i = 0; i < 3; i++)
        {
            await TickAndDrainAsync(scheduler);
        }

        var run = Assert.Single(h.Store.AllRuns());
        Assert.Equal(JobRunStatus.Succeeded, run.Status);
        Assert.Equal(h.Clock.Now.AddHours(1), h.Store.JobRow("hourly").NextRunAt);
    }

    [Fact]
    public async Task A_due_job_with_an_active_run_is_not_claimed_until_the_run_ends()
    {
        var h = new Harness();
        h.Runtime.JobScripts = _ => new FakeContainerRuntime.JobScript { Hang = true };
        h.AddJob("busy", schedule: "0 * * * *", nextRunAt: h.Clock.Now.AddMinutes(-1));
        var manual = await h.EnqueueManualAsync("busy");
        var (scheduler, _) = h.Scheduler();

        await scheduler.RunOnceAsync();
        await Eventually(() => h.Runtime.RunningJobContainers == 1, "the manual run to start");
        await scheduler.RunOnceAsync();
        Assert.Single(h.Store.AllRuns());

        h.Runtime.ExitJobContainer($"job-busy-{manual}", 0);
        await scheduler.WaitForDriversAsync().WaitAsync(Wait);
        h.Runtime.JobScripts = _ => new FakeContainerRuntime.JobScript();
        await TickAndDrainAsync(scheduler);

        Assert.Equal(2, h.Store.AllRuns().Count);
        Assert.Single(h.Store.AllRuns(), r => r.Trigger == JobRunTrigger.Schedule);
    }

    [Fact]
    public async Task Max_concurrency_holds_and_the_rest_wait_in_queued()
    {
        var h = new Harness();
        h.Runtime.JobScripts = _ => new FakeContainerRuntime.JobScript { Hang = true };
        var ids = new List<long>();
        for (var i = 1; i <= 5; i++)
        {
            h.AddJob($"job-{i}");
            ids.Add(await h.EnqueueManualAsync($"job-{i}"));
        }

        var (scheduler, _) = h.Scheduler(maxConcurrent: 2);
        await scheduler.RunOnceAsync();
        await scheduler.RunOnceAsync();
        await Eventually(() => h.Runtime.RunningJobContainers == 2, "two runs to start");

        Assert.Equal(2, h.Store.AllRuns().Count(r => r.Status == JobRunStatus.Running));
        Assert.Equal(3, h.Store.AllRuns().Count(r => r.Status == JobRunStatus.Queued));
        // Oldest first.
        Assert.Equal(new[] { ids[0], ids[1] }, h.Store.AllRuns().Where(r => r.Status == JobRunStatus.Running).Select(r => r.Id));

        h.Runtime.ExitJobContainer($"job-job-1-{ids[0]}", 0);
        await Eventually(() => h.Store.Run(ids[0]).Status == JobRunStatus.Succeeded, "the first run to finish");
        await Eventually(() => scheduler.DrivenRunIds.Count == 1, "the first driver to end");
        await scheduler.RunOnceAsync();
        await Eventually(() => h.Runtime.RunningJobContainers == 2, "the next run to start");

        Assert.Equal(JobRunStatus.Running, h.Store.Run(ids[2]).Status);
        Assert.Equal(2, h.Store.AllRuns().Count(r => r.Status == JobRunStatus.Queued));
        Assert.Equal(2, h.Runtime.MaxRunningJobContainers);
    }

    [Fact]
    public async Task Operator_force_delete_mid_run_cancels_the_run()
    {
        var h = new Harness();
        h.Runtime.JobScripts = _ => new FakeContainerRuntime.JobScript { Hang = true };
        h.AddJob("mirror");
        var id = await h.EnqueueManualAsync("mirror");
        var (scheduler, _) = h.Scheduler();
        await scheduler.RunOnceAsync();
        await Eventually(() => h.Runtime.RunningJobContainers == 1, "the container to run");

        // What DELETE ?force=true does: cancel the active run, then remove the job.
        await h.Store.CancelRunAsync(id, h.Clock.Now);
        await h.Store.DeleteAsync("mirror");
        await TickAndDrainAsync(scheduler);

        Assert.Equal(JobRunStatus.Cancelled, h.Store.Run(id).Status);
    }

    [Fact]
    public async Task A_claimed_run_whose_job_was_deleted_fails_without_running()
    {
        var h = new Harness();
        h.AddJob("mirror");
        var id = await h.EnqueueManualAsync("mirror");
        await h.Store.DeleteAsync("mirror");
        var (scheduler, _) = h.Scheduler();

        await TickAndDrainAsync(scheduler);

        var run = h.Store.Run(id);
        Assert.Equal(JobRunStatus.Failed, run.Status);
        Assert.Contains("no longer exists", run.Detail);
        Assert.Empty(h.Runtime.JobSpecs);
    }

    // ---------- leadership gate ----------

    [Fact]
    public async Task A_follower_or_a_stale_leader_answer_claims_nothing()
    {
        var h = new Harness();
        h.AddJob("due", schedule: "0 * * * *", nextRunAt: h.Clock.Now.AddMinutes(-1));
        h.AddJob("manual");
        var id = await h.EnqueueManualAsync("manual");

        var (follower, _) = h.Scheduler(leader: false);
        await TickAndDrainAsync(follower);

        var (stale, status) = h.Scheduler(leader: true);
        status.At = h.Clock.Now - h.Reconciler.InstanceStaleThreshold - TimeSpan.FromSeconds(1);
        await TickAndDrainAsync(stale);

        var (neverEvaluated, unevaluated) = h.Scheduler(leader: true);
        unevaluated.At = null;
        await TickAndDrainAsync(neverEvaluated);

        Assert.Equal(JobRunStatus.Queued, h.Store.Run(id).Status);
        Assert.Single(h.Store.AllRuns());
        Assert.Empty(h.Runtime.JobSpecs);
    }

    [Fact]
    public async Task The_hosted_loop_ticks_on_the_configured_interval()
    {
        var h = new Harness();
        h.AddJob("mirror");
        var id = await h.EnqueueManualAsync("mirror");
        var (scheduler, _) = h.Scheduler();

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            await Eventually(() => h.Store.Run(id).Status == JobRunStatus.Succeeded, "the loop to run the job");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Detail_is_truncated_from_the_front_to_two_kib()
    {
        var long_ = new string('x', 5000) + "\nlast line";
        var truncated = JobSchedulerService.TruncateDetail(long_)!;
        Assert.True(Encoding.UTF8.GetByteCount(truncated) <= JobSchedulerService.MaxDetailBytes);
        Assert.EndsWith("last line", truncated);
        Assert.Equal("short", JobSchedulerService.TruncateDetail("short"));
        Assert.Null(JobSchedulerService.TruncateDetail(null));
    }
}
