using System.Text.Json;
using Gateway.Api.Data;
using Gateway.Api.Instances;
using Gateway.Api.Load;
using Gateway.Api.Management;
using Gateway.Api.Reconcile;
using Gateway.Api.Resources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Tests;

/// <summary>
/// The load scheduler (docs/load.md section 3) against fakes: <see cref="FakeLoadStore"/>,
/// <see cref="FakeFleetScaler"/> for both groups, <see cref="FakeSecretWriter"/>,
/// <see cref="FakePresenceRegistry"/>, <see cref="FakeInstanceStatusStore"/>, a stub
/// pre-flight and leader, and a hand-cranked clock. Every tick is driven directly with
/// <see cref="LoadSchedulerService.RunOnceAsync"/>.
/// </summary>
public class LoadSchedulerTests
{
    private const string GeneratorGroup = "load-generators-placeholder";
    private const string FleetGroup = "gateway-fleet-placeholder";
    private const string SpecSecret = "load-generator-spec-placeholder";
    private const string BaseUrl = "https://gateway.example.com";
    private const string Token = "run-token-placeholder-value";
    private const string Scenario = "tracker";

    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 2, 0, 0, TimeSpan.Zero);

    private sealed class StubLeaderStatus : ILeaderStatus
    {
        public bool IsLeader { get; set; } = true;
        public DateTimeOffset? At { get; set; }

        public LeaderResolution Current => new(IsLeader, IsLeader ? "i-0placeholder0" : null);
        public DateTimeOffset? EvaluatedAt => At;
    }

    private sealed class StubPreflight : ILoadPreflight
    {
        public List<PreflightCheckResult> Checks { get; } = new()
        {
            new PreflightCheckResult("generator_group", PreflightStatus.Green, "ok", null),
        };

        public List<LoadPreflightRequest> Requests { get; } = new();

        public Task<IReadOnlyList<PreflightCheckResult>> EvaluateAsync(LoadPreflightRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult<IReadOnlyList<PreflightCheckResult>>(Checks.ToList());
        }
    }

    private sealed class CapturingLogger : ILogger<LoadSchedulerService>
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = new();

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_gate)
                {
                    return _lines.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
            }
        }
    }

    private sealed class Harness
    {
        private readonly ServiceProvider _provider;
        private int _instances;

        public Harness(Action<LoadOptions>? configure = null)
        {
            Options = new LoadOptions
            {
                GeneratorGroup = GeneratorGroup,
                GeneratorSpecSecret = SpecSecret,
                GatewayBaseUrl = BaseUrl,
            };
            configure?.Invoke(Options);

            Scaler.Groups[GeneratorGroup] = new FleetGroupSize(Min: 0, Desired: 0, Max: 2, InService: 0);
            Scaler.Groups[FleetGroup] = new FleetGroupSize(Min: 2, Desired: 2, Max: 2, InService: 2);
            var scenario = LoadTestHost.Scenario(Scenario);
            scenario.Image = "registry.example.com/gateway-job-runner";
            scenario.Tag = "v7";
            Store.Seed(scenario);

            var services = new ServiceCollection();
            services.AddSingleton<ILoadStore>(Store);
            services.AddSingleton<IInstanceStatusStore>(Status);
            services.AddSingleton<IResourceAuditStore>(Audit);
            services.AddSingleton<ILoadPreflight>(Preflight);
            _provider = services.BuildServiceProvider();

            Scheduler = NewScheduler();
        }

        public FakeLoadStore Store { get; } = new();
        public FakeFleetScaler Scaler { get; } = new();
        public FakeSecretWriter Secrets { get; } = new();
        public FakePresenceRegistry Presence { get; } = new();
        public FakeInstanceStatusStore Status { get; } = new();
        public InMemoryResourceAuditStore Audit { get; } = new();
        public StubPreflight Preflight { get; } = new();
        public FakeLoadMetrics Metrics { get; } = new();
        public JobTestHost.RecordingPublisher Events { get; } = new();
        public ManualTimeProvider Clock { get; } = new() { Now = T0 };
        public StubLeaderStatus Leader { get; } = new();
        public CapturingLogger Logs { get; } = new();
        public ReconcilerOptions Reconciler { get; } = new();
        public LoadOptions Options { get; }
        public LoadSchedulerService Scheduler { get; private set; }

        /// <summary>A fresh scheduler process on the same state (a leader restart).</summary>
        public LoadSchedulerService NewScheduler() =>
            Scheduler = new LoadSchedulerService(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                Scaler,
                Secrets,
                new FleetGroupResolver(new ResourcesOptions { FleetGroup = FleetGroup }, new FakeFleetGroupSource(), Clock),
                Leader,
                Options,
                Reconciler,
                Metrics,
                Logs,
                Clock,
                migrationGate: null,
                publisher: Events,
                presence: Presence);

        public async Task TickAsync(int times = 1)
        {
            for (var i = 0; i < times; i++)
            {
                Leader.At = Clock.Now;
                await Scheduler.RunOnceAsync();
            }
        }

        public void Advance(TimeSpan by) => Clock.Now += by;

        /// <summary>A queued run as Start writes it.</summary>
        public LoadRun Queue(
            int generators = 2,
            int ramp = 60,
            int hold = 120,
            int? fleet = null,
            bool restore = true,
            string runParams = "{}",
            bool validation = false,
            DateTimeOffset? hardStopAt = null)
        {
            var run = LoadTestHost.Run(0, LoadRunStatus.Queued, scenario: Scenario, generators: generators, validation: validation, token: Token);
            run.RampSeconds = ramp;
            run.HoldSeconds = hold;
            run.FleetSizeRequested = fleet;
            run.RestoreFleet = restore;
            run.Params = runParams;
            run.QueuedAt = Clock.Now;
            run.HardStopAt = hardStopAt ?? Clock.Now + TimeSpan.FromSeconds(ramp + hold) + TimeSpan.FromMinutes(10);
            run.SpecToken = Token;
            run.Timeline = LoadJson.AppendTimeline(null, Clock.Now, LoadRunStatus.Queued, "alice-admin");
            return Store.Seed(run);
        }

        /// <summary><paramref name="count"/> more fleet instances with a heartbeat now.</summary>
        public void Heartbeat(int count = 1, DateTimeOffset? at = null)
        {
            for (var i = 0; i < count; i++)
            {
                Status.Seed(new InstanceStatus
                {
                    InstanceId = $"i-0placeholder{_instances++}",
                    IsLeader = _instances == 1,
                    HeartbeatAt = at ?? Clock.Now,
                    Services = "[{\"name\":\"svc-a\",\"digest\":null,\"state\":\"running\",\"startedAt\":null,\"restarts\":0}]",
                });
            }
        }

        public async Task CheckInAllAsync(LoadRun run)
        {
            for (var i = 0; i < run.Generators; i++)
            {
                await Store.CheckInAsync(run.Id, $"i-0generator{i}");
            }
        }

        public Task LineAsync(long runId, int index = 0) =>
            Store.AppendStatAsync(new LoadRunStat
            {
                RunId = runId,
                Source = LoadStatSource.Generator,
                Index = index,
                At = Clock.Now,
                Body = LoadTestHost.StatsLine(runId, index, Clock.Now, 10, "{}"),
            });

        public void InService(string group, int count) =>
            Scaler.Groups[group] = Scaler.Groups[group] with { InService = count };

        public string StatusOf(long id) => Store.Run(id).Status;

        /// <summary>Every mutating scaler call, in order.</summary>
        public string[] Sets => Scaler.CallLog.Where(c => c.StartsWith("set-", StringComparison.Ordinal)).ToArray();

        public string[] RunEvents(long id) => Events.All
            .Where(e => e.Channel == LoadEndpoints.OpsLoadChannel && e.Event == "run")
            .Select(e => JsonSerializer.SerializeToElement(e.Data))
            .Where(e => e.GetProperty("runId").GetInt64() == id)
            .Select(e => e.GetProperty("status").GetString()!)
            .ToArray();

        public string[] Timeline(long id) =>
            LoadJson.ParseTimeline(Store.Run(id).Timeline).Select(e => e.Status).Distinct().ToArray();

        /// <summary>Claim (and pre-flight), then drive ticks until the run reaches <paramref name="status"/>.</summary>
        public async Task DriveToAsync(LoadRun run, string status)
        {
            for (var i = 0; i < 20 && StatusOf(run.Id) != status; i++)
            {
                switch (StatusOf(run.Id))
                {
                    case LoadRunStatus.GeneratorsUp when Store.Run(run.Id).SpecToken is null:
                        await CheckInAllAsync(run);
                        break;
                    case LoadRunStatus.Ramping:
                        Advance(TimeSpan.FromSeconds(run.RampSeconds));
                        await LineAsync(run.Id);
                        break;
                }

                await TickAsync();
            }

            Assert.Equal(status, StatusOf(run.Id));
        }
    }

    // ---------- the happy path ----------

    [Fact]
    public async Task HappyPath_WithAFleetSize_WalksEveryState_WithTheExactScalerCalls_AndRestoresTheFleet()
    {
        var h = new Harness();
        h.Heartbeat(2);
        var run = h.Queue(generators: 2, ramp: 60, hold: 120, fleet: 3, restore: true);

        await h.TickAsync();
        Assert.Equal(LoadRunStatus.FleetUp, h.StatusOf(run.Id));
        Assert.Equal(LoadSchedulerService.Actor, LoadJson.ParseTimeline(h.Store.Run(run.Id).Timeline)[1].By);
        var preflight = Assert.Single(h.Preflight.Requests);
        Assert.Equal((Scenario, LoadTestHost.Target, LoadTestHost.Target, 2, 3), (preflight.Scenario, preflight.Target, preflight.Confirm, preflight.Generators, preflight.FleetSize));
        Assert.Empty(h.Sets);

        // fleet_up: the size before is recorded, then min, max and desired together.
        await h.TickAsync();
        Assert.Equal(2, h.Store.Run(run.Id).FleetSizeBefore);
        Assert.Equal(new[] { $"set-group-size {FleetGroup} 3 3 3" }, h.Sets);

        // Waits until the requested count heartbeats.
        await h.TickAsync();
        Assert.Equal(LoadRunStatus.FleetUp, h.StatusOf(run.Id));
        h.Heartbeat();
        await h.TickAsync();
        Assert.Equal(LoadRunStatus.GeneratorsUp, h.StatusOf(run.Id));
        Assert.Single(h.Sets);

        // generators_up: the secret, then desired only for the generator group.
        await h.TickAsync();
        Assert.Single(h.Secrets.Writes);
        Assert.Null(h.Store.Run(run.Id).SpecToken);
        Assert.Equal($"set-desired {GeneratorGroup} 2", h.Sets[^1]);
        h.InService(GeneratorGroup, 2);
        await h.TickAsync();
        Assert.Equal(LoadRunStatus.GeneratorsUp, h.StatusOf(run.Id));

        await h.CheckInAllAsync(run);
        h.Advance(TimeSpan.FromSeconds(5));
        await h.TickAsync();
        Assert.Equal(LoadRunStatus.Ramping, h.StatusOf(run.Id));
        Assert.Equal(h.Clock.Now, h.Store.Run(run.Id).RampStartedAt);

        // ramping leaves after ramp_seconds.
        h.Advance(TimeSpan.FromSeconds(59));
        await h.LineAsync(run.Id);
        await h.TickAsync();
        Assert.Equal(LoadRunStatus.Ramping, h.StatusOf(run.Id));
        h.Advance(TimeSpan.FromSeconds(1));
        await h.LineAsync(run.Id);
        await h.TickAsync();
        Assert.Equal(LoadRunStatus.Holding, h.StatusOf(run.Id));

        // holding leaves after hold_seconds, setting the generators to 0 first.
        for (var i = 0; i < 12; i++)
        {
            h.Advance(TimeSpan.FromSeconds(10));
            await h.LineAsync(run.Id);
            await h.TickAsync();
        }

        Assert.Equal(LoadRunStatus.Draining, h.StatusOf(run.Id));
        Assert.Null(h.Store.Run(run.Id).StopReason);
        Assert.Equal($"set-desired {GeneratorGroup} 0", h.Sets[^1]);

        // draining: the fleet goes back, and the run waits for zero in service.
        await h.TickAsync();
        Assert.Equal(LoadRunStatus.Draining, h.StatusOf(run.Id));
        Assert.Equal($"set-group-size {FleetGroup} 2 2 2", h.Sets[^1]);
        h.InService(GeneratorGroup, 0);
        await h.TickAsync();
        Assert.Equal(LoadRunStatus.Succeeded, h.StatusOf(run.Id));

        Assert.Equal(new[]
        {
            $"set-group-size {FleetGroup} 3 3 3",
            $"set-desired {GeneratorGroup} 2",
            $"set-desired {GeneratorGroup} 0",
            $"set-group-size {FleetGroup} 2 2 2",
        }, h.Sets);

        var finished = h.Store.Run(run.Id);
        Assert.Equal(h.Clock.Now, finished.FinishedAt);
        Assert.Equal(new[] { "queued", "fleet_up", "generators_up", "ramping", "holding", "draining", "succeeded" }, h.Timeline(run.Id));
        Assert.Equal(new[] { "fleet_up", "generators_up", "ramping", "holding", "draining", "succeeded" }, h.RunEvents(run.Id));

        var metric = Assert.Single(h.Metrics.Runs);
        Assert.Equal((Scenario, LoadRunStatus.Succeeded), (metric.Scenario, metric.Status));
        Assert.Equal(finished.FinishedAt - finished.StartedAt, metric.Duration);
        Assert.Empty(h.Scheduler.DrivenRunIds);

        // Nothing more happens once terminal: the guard sees desired 0.
        var calls = h.Sets.Length;
        await h.TickAsync();
        Assert.Equal(calls, h.Sets.Length);
    }

    [Fact]
    public async Task HappyPath_WithoutAFleetSize_SkipsFleetUp_AndNeverTouchesTheFleet()
    {
        var h = new Harness();
        var run = h.Queue(ramp: 0, hold: 10);

        await h.TickAsync();
        Assert.Equal(LoadRunStatus.GeneratorsUp, h.StatusOf(run.Id));
        await h.DriveToAsync(run, LoadRunStatus.Holding);
        h.Advance(TimeSpan.FromSeconds(10));
        await h.LineAsync(run.Id);
        await h.TickAsync();
        await h.TickAsync();

        Assert.Equal(LoadRunStatus.Succeeded, h.StatusOf(run.Id));
        Assert.Equal(new[] { $"set-desired {GeneratorGroup} 2", $"set-desired {GeneratorGroup} 0" }, h.Sets);
        Assert.DoesNotContain(h.Scaler.CallLog, c => c.Contains(FleetGroup, StringComparison.Ordinal));
        Assert.DoesNotContain("fleet_up", h.Timeline(run.Id));
    }

    [Fact]
    public async Task FleetSize_WithoutRestore_LeavesTheFleetAtTheRequestedSize()
    {
        var h = new Harness();
        h.Heartbeat(3);
        var run = h.Queue(ramp: 0, hold: 0, fleet: 3, restore: false);

        await h.DriveToAsync(run, LoadRunStatus.Holding);
        await h.LineAsync(run.Id);
        await h.TickAsync(2);

        Assert.Equal(LoadRunStatus.Succeeded, h.StatusOf(run.Id));
        Assert.Equal(2, h.Store.Run(run.Id).FleetSizeBefore);
        Assert.Equal(new[]
        {
            $"set-group-size {FleetGroup} 3 3 3",
            $"set-desired {GeneratorGroup} 2",
            $"set-desired {GeneratorGroup} 0",
        }, h.Sets);
    }

    [Fact]
    public async Task ValidationRun_Succeeding_RecordsItselfOnTheScenario()
    {
        var h = new Harness();
        var run = h.Queue(generators: 1, ramp: 0, hold: 0, validation: true);

        await h.DriveToAsync(run, LoadRunStatus.Holding);
        await h.LineAsync(run.Id);
        await h.TickAsync(2);

        Assert.Equal(LoadRunStatus.Succeeded, h.StatusOf(run.Id));
        Assert.Equal(run.Id, (await h.Store.GetScenarioAsync(Scenario))!.LastValidationRunId);
    }

    // ---------- claim ----------

    [Fact]
    public async Task Claim_IsRefused_WhileAnotherRunIsNonTerminal()
    {
        var h = new Harness();
        var other = LoadTestHost.Run(0, LoadRunStatus.Holding, scenario: Scenario);
        h.Store.Seed(other);
        var queued = h.Queue();

        await h.TickAsync();

        Assert.Equal(LoadRunStatus.Queued, h.StatusOf(queued.Id));
        Assert.Null(h.Store.Run(queued.Id).StartedAt);
        Assert.Empty(h.RunEvents(queued.Id));
        Assert.Empty(h.Preflight.Requests);
    }

    [Fact]
    public async Task Claim_PreflightRed_FailsTheRun_WithTheCheckName_AfterGeneratorsToZero()
    {
        var h = new Harness();
        h.Preflight.Checks.Add(new PreflightCheckResult("fleet_healthy", PreflightStatus.Red, "stale", "wait"));
        var run = h.Queue();

        await h.TickAsync();
        var draining = h.Store.Run(run.Id);
        Assert.Equal(LoadRunStatus.Draining, draining.Status);
        Assert.Equal((LoadStopReason.Failed, "fleet_healthy"), (draining.StopReason, draining.Detail));
        Assert.Equal(new[] { $"set-desired {GeneratorGroup} 0" }, h.Sets);

        await h.TickAsync();
        Assert.Equal(LoadRunStatus.Failed, h.StatusOf(run.Id));
        Assert.Equal("fleet_healthy", h.Store.Run(run.Id).Detail);
        Assert.Empty(h.Secrets.Writes);
        Assert.Equal(LoadRunStatus.Failed, Assert.Single(h.Metrics.Runs).Status);
        Assert.Equal(new[] { "generators_up", "draining", "failed" }, h.RunEvents(run.Id));
    }

    [Fact]
    public async Task NotLeader_DoesNothing()
    {
        var h = new Harness();
        h.Leader.IsLeader = false;
        h.Scaler.Groups[GeneratorGroup] = h.Scaler.Groups[GeneratorGroup] with { Desired = 2 };
        var run = h.Queue();

        await h.TickAsync();

        Assert.Equal(LoadRunStatus.Queued, h.StatusOf(run.Id));
        Assert.Empty(h.Scaler.CallLog);

        // A stale leader evaluation is not leadership either.
        h.Leader.IsLeader = true;
        h.Leader.At = h.Clock.Now - h.Reconciler.InstanceStaleThreshold;
        await h.Scheduler.RunOnceAsync();
        Assert.Equal(LoadRunStatus.Queued, h.StatusOf(run.Id));
    }

    // ---------- generators_up ----------

    [Fact]
    public async Task SpecSecret_IsWrittenOnce_WithTheDocumentedJson_AndNeverLogged()
    {
        var h = new Harness();
        var run = h.Queue();

        await h.TickAsync(4);

        var (reference, value) = Assert.Single(h.Secrets.Writes);
        Assert.Equal(SpecSecret, reference);
        var spec = JsonDocument.Parse(value).RootElement;
        Assert.Equal(new[] { "runId", "gatewayBaseUrl", "token", "image", "tag" }, spec.EnumerateObject().Select(p => p.Name));
        Assert.Equal(run.Id, spec.GetProperty("runId").GetInt64());
        Assert.Equal(BaseUrl, spec.GetProperty("gatewayBaseUrl").GetString());
        Assert.Equal(Token, spec.GetProperty("token").GetString());
        Assert.Equal("registry.example.com/gateway-job-runner", spec.GetProperty("image").GetString());
        Assert.Equal("v7", spec.GetProperty("tag").GetString());
        Assert.Equal(run.TokenHash, LoadEndpoints.TokenHash(spec.GetProperty("token").GetString()!));

        // Through the end of the run, the token never reaches a log line or an event.
        await h.DriveToAsync(run, LoadRunStatus.Holding);
        await h.Store.RequestStopAsync(run.Id, "alice-admin", h.Clock.Now);
        await h.TickAsync(2);
        Assert.Single(h.Secrets.Writes);
        Assert.NotEmpty(h.Logs.Lines);
        Assert.DoesNotContain(h.Logs.Lines, l => l.Contains(Token, StringComparison.Ordinal));
        Assert.DoesNotContain(h.Events.All, e => JsonSerializer.Serialize(e.Data).Contains(Token, StringComparison.Ordinal));
        Assert.Null(h.Store.Run(run.Id).SpecToken);
    }

    [Theory]
    [InlineData(null, BaseUrl, "Load:GeneratorSpecSecret is not set")]
    [InlineData(SpecSecret, null, "Load:GatewayBaseUrl is not set")]
    public async Task SpecSecret_MissingSetting_FailsTheRun(string? secret, string? baseUrl, string detail)
    {
        var h = new Harness(o =>
        {
            o.GeneratorSpecSecret = secret;
            o.GatewayBaseUrl = baseUrl;
        });
        var run = h.Queue();

        await h.TickAsync(3);

        Assert.Equal(LoadRunStatus.Failed, h.StatusOf(run.Id));
        Assert.Equal(detail, h.Store.Run(run.Id).Detail);
        Assert.Empty(h.Secrets.Writes);
        Assert.Equal(new[] { $"set-desired {GeneratorGroup} 0" }, h.Sets);
    }

    [Fact]
    public async Task CheckInTimeout_FailsTheRun_AfterGeneratorsToZero()
    {
        var h = new Harness();
        var run = h.Queue();

        await h.TickAsync(2);
        await h.Store.CheckInAsync(run.Id, "i-0generator0");
        h.Advance(h.Options.CheckInTimeout - TimeSpan.FromSeconds(1));
        await h.TickAsync();
        Assert.Equal(LoadRunStatus.GeneratorsUp, h.StatusOf(run.Id));

        h.Advance(TimeSpan.FromSeconds(1));
        await h.TickAsync();
        var draining = h.Store.Run(run.Id);
        Assert.Equal((LoadRunStatus.Draining, LoadStopReason.Failed, LoadSchedulerService.CheckInTimeoutDetail),
            (draining.Status, draining.StopReason, draining.Detail));
        Assert.Equal($"set-desired {GeneratorGroup} 0", h.Sets[^1]);

        await h.TickAsync();
        Assert.Equal(LoadRunStatus.Failed, h.StatusOf(run.Id));
        Assert.Equal(LoadSchedulerService.CheckInTimeoutDetail, h.Store.Run(run.Id).Detail);
    }

    // ---------- exits ----------

    [Fact]
    public async Task HardClock_DuringHolding_Expires()
    {
        var h = new Harness();
        var run = h.Queue(ramp: 0, hold: 3600, hardStopAt: T0.AddMinutes(5));

        await h.DriveToAsync(run, LoadRunStatus.Holding);
        h.Advance(T0.AddMinutes(5) - h.Clock.Now);
        await h.LineAsync(run.Id);
        await h.TickAsync();

        var draining = h.Store.Run(run.Id);
        Assert.Equal((LoadRunStatus.Draining, LoadStopReason.HardClock), (draining.Status, draining.StopReason));
        Assert.Equal($"set-desired {GeneratorGroup} 0", h.Sets[^1]);
        await h.TickAsync();
        Assert.Equal(LoadRunStatus.Expired, h.StatusOf(run.Id));
        Assert.Equal(LoadRunStatus.Expired, Assert.Single(h.Metrics.Runs).Status);
    }

    [Fact]
    public async Task StaleStats_DuringRamping_MarksTheRunLost()
    {
        var h = new Harness();
        var run = h.Queue(ramp: 600, hold: 60);

        await h.TickAsync(2);
        await h.CheckInAllAsync(run);
        await h.TickAsync();
        Assert.Equal(LoadRunStatus.Ramping, h.StatusOf(run.Id));

        // A line keeps it fresh; then nothing for longer than Load:StatsStaleAfter.
        h.Advance(TimeSpan.FromSeconds(30));
        await h.LineAsync(run.Id);
        h.Advance(h.Options.StatsStaleAfter);
        await h.TickAsync();
        Assert.Equal(LoadRunStatus.Ramping, h.StatusOf(run.Id));

        h.Advance(TimeSpan.FromSeconds(1));
        await h.TickAsync();
        var draining = h.Store.Run(run.Id);
        Assert.Equal((LoadRunStatus.Draining, LoadStopReason.Lost), (draining.Status, draining.StopReason));
        Assert.Equal($"set-desired {GeneratorGroup} 0", h.Sets[^1]);
        await h.TickAsync();
        Assert.Equal(LoadRunStatus.Lost, h.StatusOf(run.Id));
    }

    [Theory]
    [InlineData(LoadRunStatus.FleetUp)]
    [InlineData(LoadRunStatus.GeneratorsUp)]
    [InlineData(LoadRunStatus.Ramping)]
    [InlineData(LoadRunStatus.Holding)]
    public async Task OperatorStop_InEveryRunningState_EndsAtGeneratorsZero_ThenStopped(string state)
    {
        var h = new Harness();
        h.Heartbeat(2);
        var run = h.Queue(ramp: 600, hold: 600, fleet: state == LoadRunStatus.FleetUp ? 3 : null);
        await h.TickAsync();
        if (state != LoadRunStatus.FleetUp)
        {
            await h.DriveToAsync(run, state);
            await h.LineAsync(run.Id);
        }

        Assert.Equal(state, h.StatusOf(run.Id));
        await h.Store.RequestStopAsync(run.Id, "alice-admin", h.Clock.Now);
        await h.TickAsync();

        var draining = h.Store.Run(run.Id);
        Assert.Equal((LoadRunStatus.Draining, LoadStopReason.Operator), (draining.Status, draining.StopReason));
        Assert.Equal($"set-desired {GeneratorGroup} 0", h.Sets[^1]);

        await h.TickAsync();
        Assert.Equal(LoadRunStatus.Stopped, h.StatusOf(run.Id));
        Assert.Equal(0, h.Scaler.Groups[GeneratorGroup].Desired);
        Assert.Equal(LoadRunStatus.Stopped, h.RunEvents(run.Id)[^1]);
    }

    // ---------- gaining leadership ----------

    [Fact]
    public async Task LeaderRestart_AdoptsAHoldingRunWithFreshStats()
    {
        var h = new Harness();
        var run = h.Queue(ramp: 0, hold: 600);
        await h.DriveToAsync(run, LoadRunStatus.Holding);
        var sets = h.Sets.Length;

        h.NewScheduler();
        h.Advance(TimeSpan.FromSeconds(10));
        await h.LineAsync(run.Id);
        await h.TickAsync();

        Assert.Equal(LoadRunStatus.Holding, h.StatusOf(run.Id));
        Assert.Equal(new[] { run.Id }, h.Scheduler.DrivenRunIds);
        Assert.Equal(sets, h.Sets.Length);

        // And it continues: the hold ends on time.
        h.Advance(TimeSpan.FromSeconds(590));
        await h.LineAsync(run.Id);
        await h.TickAsync(2);
        Assert.Equal(LoadRunStatus.Succeeded, h.StatusOf(run.Id));
    }

    [Theory]
    [InlineData(LoadRunStatus.GeneratorsUp, false)]
    [InlineData(LoadRunStatus.Ramping, true)]
    public async Task LeaderRestart_DrainsARunItCannotReattach(string state, bool staleStats)
    {
        var h = new Harness();
        var run = h.Queue(ramp: 600, hold: 600);
        await h.TickAsync();
        await h.DriveToAsync(run, state);

        h.NewScheduler();
        if (staleStats)
        {
            h.Advance(h.Options.StatsStaleAfter + TimeSpan.FromSeconds(1));
        }

        await h.TickAsync();
        var draining = h.Store.Run(run.Id);
        Assert.Equal((LoadRunStatus.Draining, LoadStopReason.LeaderRestart), (draining.Status, draining.StopReason));
        Assert.Equal($"set-desired {GeneratorGroup} 0", h.Sets[^1]);

        await h.TickAsync();
        Assert.Equal(LoadRunStatus.Lost, h.StatusOf(run.Id));
    }

    [Fact]
    public async Task LosingAndRegainingLeadership_CountsAsGainingIt()
    {
        var h = new Harness();
        var run = h.Queue();
        await h.TickAsync(2);
        Assert.Equal(LoadRunStatus.GeneratorsUp, h.StatusOf(run.Id));

        h.Leader.IsLeader = false;
        await h.TickAsync();
        Assert.Empty(h.Scheduler.DrivenRunIds);

        h.Leader.IsLeader = true;
        await h.TickAsync();
        Assert.Equal(LoadStopReason.LeaderRestart, h.Store.Run(run.Id).StopReason);
    }

    // ---------- the orphan guard ----------

    [Fact]
    public async Task OrphanGuard_SetsTheGeneratorGroupToZero_AndAudits_WhenNoRunIsActive()
    {
        var h = new Harness();
        h.Store.Seed(LoadTestHost.Run(0, LoadRunStatus.Succeeded, scenario: Scenario));
        h.Scaler.Groups[GeneratorGroup] = new FleetGroupSize(0, 2, 2, 1);

        await h.TickAsync();

        Assert.Equal(new[] { $"set-desired {GeneratorGroup} 0" }, h.Sets);
        var row = Assert.Single(await h.Audit.ListAsync(10));
        Assert.Equal((LoadSchedulerService.Actor, LoadSchedulerService.GeneratorsResource, LoadSchedulerService.OrphanGuardAction),
            (row.Actor, row.Resource, row.Action));
        Assert.Equal("desired 0", row.After);
        Assert.Contains(GeneratorGroup, row.Detail);
        Assert.Contains(h.Events.All, e => e.Channel == ResourceEndpoints.OpsResourcesChannel && e.Event == "change");

        // Desired is 0 now: nothing more to do.
        await h.TickAsync();
        Assert.Single(h.Sets);
        Assert.Single(await h.Audit.ListAsync(10));
    }

    [Fact]
    public async Task OrphanGuard_LeavesTheGroupAlone_WhileARunIsNonTerminal()
    {
        var h = new Harness();
        h.Scaler.Groups[GeneratorGroup] = new FleetGroupSize(0, 2, 2, 2);
        var run = h.Queue(ramp: 600);
        await h.TickAsync(2);
        await h.CheckInAllAsync(run);
        await h.TickAsync();

        Assert.Equal(LoadRunStatus.Ramping, h.StatusOf(run.Id));
        Assert.Empty(h.Sets);
        Assert.Empty(await h.Audit.ListAsync(10));
    }

    // ---------- fleet samples ----------

    [Fact]
    public async Task FleetSamples_AreWrittenEveryInterval_FromFreshHeartbeats_WithTheChannelCount_AndPublished()
    {
        var h = new Harness();
        h.Heartbeat(2);
        h.Heartbeat(1, at: T0 - TimeSpan.FromMinutes(10));
        h.Presence.Counts["tracker:location"] = 42;
        var run = h.Queue(ramp: 600, runParams: "{\"channel\":\"tracker:location\"}");

        await h.TickAsync();
        await h.TickAsync();
        var samples = await h.Store.ListStatsAsync(run.Id, null, null, LoadStatSource.Fleet, null);
        var sample = Assert.Single(samples);
        Assert.Null(sample.Index);
        Assert.Equal(T0, sample.At);

        var body = JsonDocument.Parse(sample.Body).RootElement;
        Assert.Equal(T0, body.GetProperty("at").GetDateTimeOffset());
        Assert.Equal(42, body.GetProperty("channelMembers").GetInt32());
        var instances = body.GetProperty("instances").EnumerateArray().ToList();
        Assert.Equal(new[] { "i-0placeholder0", "i-0placeholder1" }, instances.Select(i => i.GetProperty("instanceId").GetString()));
        Assert.True(instances[0].GetProperty("leader").GetBoolean());
        Assert.False(instances[1].GetProperty("leader").GetBoolean());
        Assert.Equal(1, instances[0].GetProperty("runningServices").GetInt32());
        foreach (var field in new[] { "cpu", "memory", "disk", "hubConnections" })
        {
            Assert.Equal(JsonValueKind.Null, instances[0].GetProperty(field).ValueKind);
        }

        var published = Assert.Single(h.Events.All, e => e.Channel == LoadEndpoints.OpsLoadChannel && e.Event == "fleet");
        var data = JsonSerializer.SerializeToElement(published.Data);
        Assert.Equal(run.Id, data.GetProperty("runId").GetInt64());
        Assert.Equal(42, data.GetProperty("sample").GetProperty("channelMembers").GetInt32());

        h.Advance(h.Options.FleetSampleInterval);
        await h.TickAsync();
        Assert.Equal(2, (await h.Store.ListStatsAsync(run.Id, null, null, LoadStatSource.Fleet, null)).Count);
    }

    [Fact]
    public async Task FleetSamples_WithoutAChannelParam_CarryNullMembers_AndStopWhenTerminal()
    {
        var h = new Harness();
        h.Heartbeat();
        var run = h.Queue(ramp: 0, hold: 0);

        await h.DriveToAsync(run, LoadRunStatus.Holding);
        await h.LineAsync(run.Id);
        await h.TickAsync(2);
        Assert.Equal(LoadRunStatus.Succeeded, h.StatusOf(run.Id));
        var count = (await h.Store.ListStatsAsync(run.Id, null, null, LoadStatSource.Fleet, null)).Count;
        Assert.True(count >= 1);
        var body = JsonDocument.Parse((await h.Store.ListStatsAsync(run.Id, null, null, LoadStatSource.Fleet, null))[0].Body).RootElement;
        Assert.Equal(JsonValueKind.Null, body.GetProperty("channelMembers").ValueKind);
        Assert.Empty(h.Presence.CountReads);

        h.Advance(TimeSpan.FromMinutes(1));
        await h.TickAsync();
        Assert.Equal(count, (await h.Store.ListStatsAsync(run.Id, null, null, LoadStatSource.Fleet, null)).Count);
    }

    // ---------- failures ----------

    [Fact]
    public async Task ScalerError_IsRetriedOnTheNextTick()
    {
        var h = new Harness();
        var run = h.Queue();
        await h.TickAsync();

        h.Scaler.Failing.Add(GeneratorGroup);
        await h.TickAsync(h.Options.ScaleRetryLimit - 1);
        Assert.Equal(LoadRunStatus.GeneratorsUp, h.StatusOf(run.Id));

        h.Scaler.Failing.Remove(GeneratorGroup);
        await h.TickAsync();
        Assert.Equal(LoadRunStatus.GeneratorsUp, h.StatusOf(run.Id));
        Assert.Equal(new[] { $"set-desired {GeneratorGroup} 2" }, h.Sets);
        Assert.Single(h.Secrets.Writes);
    }

    [Fact]
    public async Task ScalerError_PastTheRetryLimit_FailsTheRun_WithTheGeneratorsDrivenToZero()
    {
        var h = new Harness(o => o.ScaleRetryLimit = 3);
        h.Heartbeat(2);
        var run = h.Queue(fleet: 3);
        await h.TickAsync();

        h.Scaler.Failing.Add(FleetGroup);
        await h.TickAsync(2);
        Assert.Equal(LoadRunStatus.FleetUp, h.StatusOf(run.Id));

        await h.TickAsync();
        var draining = h.Store.Run(run.Id);
        Assert.Equal((LoadRunStatus.Draining, LoadStopReason.Failed), (draining.Status, draining.StopReason));
        Assert.Contains("3 ticks in a row", draining.Detail);
        Assert.Equal(new[] { $"set-desired {GeneratorGroup} 0" }, h.Sets);

        await h.TickAsync();
        Assert.Equal(LoadRunStatus.Failed, h.StatusOf(run.Id));
    }

    [Fact]
    public async Task ScalerError_OnTheGeneratorGroup_PastTheLimit_StillFailsTheRun_AndTheGuardFinishesTheJob()
    {
        var h = new Harness(o => o.ScaleRetryLimit = 2);
        var run = h.Queue();
        await h.TickAsync(2);
        Assert.Equal($"set-desired {GeneratorGroup} 2", h.Sets[^1]);

        // The group cannot be reached: the stop cannot set desired 0, nor can draining.
        h.Scaler.Failing.Add(GeneratorGroup);
        await h.Store.RequestStopAsync(run.Id, "alice-admin", h.Clock.Now);
        await h.TickAsync(2);
        Assert.Equal((LoadRunStatus.Draining, LoadStopReason.Failed), (h.StatusOf(run.Id), h.Store.Run(run.Id).StopReason));
        await h.TickAsync(2);
        Assert.Equal(LoadRunStatus.Failed, h.StatusOf(run.Id));

        // Once the group answers again the orphan guard sets it to 0.
        h.Scaler.Failing.Remove(GeneratorGroup);
        await h.TickAsync();
        Assert.Equal($"set-desired {GeneratorGroup} 0", h.Sets[^1]);
        Assert.Equal(LoadSchedulerService.OrphanGuardAction, Assert.Single(await h.Audit.ListAsync(10)).Action);
    }

    [Fact]
    public async Task SecretWriteError_PastTheRetryLimit_FailsTheRun_WithoutEverSettingGenerators()
    {
        var h = new Harness(o => o.ScaleRetryLimit = 2);
        h.Secrets.Error = new InvalidOperationException("access denied");
        var run = h.Queue();

        await h.TickAsync(3);
        Assert.Equal(LoadRunStatus.Draining, h.StatusOf(run.Id));
        Assert.Equal(new[] { $"set-desired {GeneratorGroup} 0" }, h.Sets);
        Assert.Null(h.Store.Run(run.Id).SpecToken);

        await h.TickAsync();
        Assert.Equal(LoadRunStatus.Failed, h.StatusOf(run.Id));
    }

    // ---------- pieces ----------

    [Theory]
    [InlineData(null, LoadRunStatus.Succeeded)]
    [InlineData(LoadStopReason.Operator, LoadRunStatus.Stopped)]
    [InlineData(LoadStopReason.HardClock, LoadRunStatus.Expired)]
    [InlineData(LoadStopReason.Failed, LoadRunStatus.Failed)]
    [InlineData(LoadStopReason.Lost, LoadRunStatus.Lost)]
    [InlineData(LoadStopReason.LeaderRestart, LoadRunStatus.Lost)]
    public void TerminalStatus_FollowsTheStopReason(string? reason, string status) =>
        Assert.Equal(status, LoadSchedulerService.TerminalStatus(reason));

    [Fact]
    public void LoadMetrics_Emf_CarriesTheDocumentedNamesAndDimensions()
    {
        var run = JsonDocument.Parse(LoadMetrics.BuildRunEmf("Gateway", Scenario, "succeeded", TimeSpan.FromSeconds(90.5), T0)).RootElement;
        var directives = run.GetProperty("_aws").GetProperty("CloudWatchMetrics").EnumerateArray().ToList();
        Assert.Equal("LoadRunCount", directives[0].GetProperty("Metrics")[0].GetProperty("Name").GetString());
        Assert.Equal(new[] { "Scenario", "Status" }, directives[0].GetProperty("Dimensions")[0].EnumerateArray().Select(d => d.GetString()));
        Assert.Equal("LoadRunDuration", directives[1].GetProperty("Metrics")[0].GetProperty("Name").GetString());
        Assert.Equal(new[] { "Scenario" }, directives[1].GetProperty("Dimensions")[0].EnumerateArray().Select(d => d.GetString()));
        Assert.Equal((Scenario, "succeeded", 1, 90.5), (run.GetProperty("Scenario").GetString(), run.GetProperty("Status").GetString(),
            run.GetProperty("LoadRunCount").GetInt32(), run.GetProperty("LoadRunDuration").GetDouble()));
        Assert.Equal("/gateway/metrics", run.GetProperty("_aws").GetProperty("LogGroupName").GetString());

        var noDuration = JsonDocument.Parse(LoadMetrics.BuildRunEmf("Gateway", Scenario, "failed", null, T0)).RootElement;
        Assert.Single(noDuration.GetProperty("_aws").GetProperty("CloudWatchMetrics").EnumerateArray());

        var lines = JsonDocument.Parse(LoadMetrics.BuildStatsEmf("Gateway", Scenario, T0)).RootElement;
        Assert.Equal("LoadGeneratorStats", lines.GetProperty("_aws").GetProperty("CloudWatchMetrics")[0].GetProperty("Metrics")[0].GetProperty("Name").GetString());
        Assert.Equal(1, lines.GetProperty("LoadGeneratorStats").GetInt32());
    }

    [Fact]
    public void LoadMetrics_SendFailure_IsDropped()
    {
        using var metrics = new LoadMetrics("not-an-endpoint", "Gateway", Microsoft.Extensions.Logging.Abstractions.NullLogger<LoadMetrics>.Instance);
        metrics.RunFinished(Scenario, "succeeded", TimeSpan.FromSeconds(1));
        metrics.GeneratorStatsReceived(Scenario);
    }

    [Fact]
    public void LoadOptions_BindTheSchedulerKeys_WithDefaults()
    {
        var defaults = LoadOptions.FromConfiguration(new ConfigurationBuilder().Build());
        Assert.Equal((TimeSpan.FromSeconds(10), (string?)null, 6), (defaults.TickInterval, defaults.GatewayBaseUrl, defaults.ScaleRetryLimit));

        var bound = LoadOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Load:TickInterval"] = "00:00:05",
            ["Load:GatewayBaseUrl"] = "https://gateway.example.com/",
            ["Load:ScaleRetryLimit"] = "3",
        }).Build());
        Assert.Equal((TimeSpan.FromSeconds(5), BaseUrl, 3), (bound.TickInterval, bound.GatewayBaseUrl, bound.ScaleRetryLimit));
    }

    [Fact]
    public void AddLoadScheduler_RegistersTheHostedService()
    {
        var services = new ServiceCollection();
        services.AddLoadScheduler();
        Assert.Contains(services, d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService));
        Assert.Contains(services, d => d.ServiceType == typeof(ISecretWriter));
    }
}
