using Gateway.Api.Data;
using Gateway.Api.Load;
using Gateway.Api.Manifest;
using Gateway.Api.Reconcile;

namespace Gateway.Api.Tests;

/// <summary>
/// The pre-flight evaluators of docs/load.md section 6, one by one against a
/// hand-built <see cref="LoadPreflightContext"/>, and <see cref="LoadPreflight"/>
/// assembling that context (heartbeat freshness, scenario and validation run lookup)
/// and turning a throwing check into red.
/// </summary>
public class LoadPreflightTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 2, 0, 0, TimeSpan.Zero);

    private const string Group = "load-generators-placeholder";

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static LoadPreflightContext Context(
        LoadPreflightRequest? request = null,
        LoadScenario? scenario = null,
        LoadRun? validationRun = null,
        ServiceManifest? target = null,
        IReadOnlyList<InstanceStatus>? fresh = null,
        IReadOnlyList<InstanceStatus>? stale = null,
        LoadOptions? options = null) => new()
    {
        Request = request ?? Request(),
        Options = options ?? new LoadOptions { GeneratorGroup = Group },
        Now = Now,
        Scenario = scenario,
        ValidationRun = validationRun,
        TargetService = target,
        FreshInstances = fresh ?? new[] { LoadTestHost.Instance("i-0a", Now) },
        StaleInstances = stale ?? Array.Empty<InstanceStatus>(),
        StaleThreshold = TimeSpan.FromSeconds(90),
    };

    private static LoadPreflightRequest Request(
        int generators = 2,
        int vus = 100,
        bool validation = false,
        string target = LoadTestHost.Target,
        string? confirm = LoadTestHost.Target) =>
        new("tracker", target, confirm, generators, vus, validation);

    private static ServiceManifest Service(bool? presence = null) => new()
    {
        Name = LoadTestHost.Target,
        Image = "registry.example.com/svc-a",
        Tag = "v1",
        Port = 8080,
        RealtimePresence = presence,
        UpdatedBy = "seed",
    };

    private static FakeGeneratorGroupScaler Scaler(GeneratorGroupSize? generators = null)
    {
        var scaler = new FakeGeneratorGroupScaler();
        scaler.Groups[Group] = generators ?? new GeneratorGroupSize(Desired: 0, Max: 2, InService: 0);
        return scaler;
    }

    // ---------- generator_group ----------

    [Fact]
    public async Task GeneratorGroup_Green_WhenEmpty_AndTheRunFitsTheMax()
    {
        var result = await new GeneratorGroupCheck(Scaler()).EvaluateAsync(Context());

        Assert.Equal(("generator_group", PreflightStatus.Green), (result.Name, result.Status));
        Assert.Null(result.Fix);
    }

    [Theory]
    [InlineData(1, 2, 0, 1, "desired 1")]
    [InlineData(0, 2, 1, 1, "1 in service")]
    [InlineData(0, 1, 0, 2, "max is 1")]
    public async Task GeneratorGroup_Red_WhenAPreviousRunLeftInstances_OrTheGroupIsTooSmall(
        int desired, int max, int inService, int generators, string says)
    {
        var check = new GeneratorGroupCheck(Scaler(new GeneratorGroupSize(desired, max, inService)));

        var result = await check.EvaluateAsync(Context(Request(generators: generators)));

        Assert.Equal(PreflightStatus.Red, result.Status);
        Assert.Contains(says, result.Message);
        Assert.NotNull(result.Fix);
    }

    [Fact]
    public async Task GeneratorGroup_AValidationRunCountsAsOneGenerator()
    {
        var check = new GeneratorGroupCheck(Scaler(new GeneratorGroupSize(Desired: 0, Max: 1, InService: 0)));

        var result = await check.EvaluateAsync(Context(Request(generators: 2, validation: true)));

        Assert.Equal(PreflightStatus.Green, result.Status);
    }

    [Fact]
    public async Task GeneratorGroup_Red_WhenUnset_Unknown_OrUnreadable()
    {
        var unset = await new GeneratorGroupCheck(Scaler()).EvaluateAsync(Context(options: new LoadOptions()));
        Assert.Equal(PreflightStatus.Red, unset.Status);
        Assert.Contains("Load:GeneratorGroup", unset.Message);

        var missing = await new GeneratorGroupCheck(new FakeGeneratorGroupScaler()).EvaluateAsync(Context());
        Assert.Equal(PreflightStatus.Red, missing.Status);
        Assert.Contains("does not exist", missing.Message);

        var nullScaler = await new GeneratorGroupCheck(new NullGeneratorGroupScaler()).EvaluateAsync(Context());
        Assert.Equal(PreflightStatus.Red, nullScaler.Status);
        Assert.Contains(NullGeneratorGroupScaler.NotConfiguredMessage, nullScaler.Message);
    }

    // ---------- fleet_healthy ----------

    [Fact]
    public async Task FleetHealthy_Green_WhenEveryInstanceHeartbeatsAndRunsTheTarget()
    {
        var check = new FleetHealthyCheck();

        var result = await check.EvaluateAsync(Context(fresh: new[]
        {
            LoadTestHost.Instance("i-0a", Now),
            LoadTestHost.Instance("i-0b", Now),
        }));

        Assert.Equal(PreflightStatus.Green, result.Status);
        Assert.Contains("2 fleet instances", result.Message);
    }

    [Fact]
    public async Task FleetHealthy_Red_NamesTheStaleInstance()
    {
        var check = new FleetHealthyCheck();

        var result = await check.EvaluateAsync(Context(stale: new[] { LoadTestHost.Instance("i-0stale", Now.AddMinutes(-5)) }));

        Assert.Equal(PreflightStatus.Red, result.Status);
        Assert.Contains("i-0stale", result.Message);
    }

    [Fact]
    public async Task FleetHealthy_Red_WhenTheTargetIsNotRunningOnAnInstance_OrNoInstanceHeartbeats()
    {
        var check = new FleetHealthyCheck();

        var notRunning = await check.EvaluateAsync(Context(fresh: new[]
        {
            LoadTestHost.Instance("i-0a", Now),
            LoadTestHost.Instance("i-0b", Now, state: "exited"),
            LoadTestHost.Instance("i-0c", Now, target: "svc-other"),
        }));
        Assert.Equal(PreflightStatus.Red, notRunning.Status);
        Assert.Contains("i-0b, i-0c", notRunning.Message);
        Assert.DoesNotContain("i-0a", notRunning.Message);

        var empty = await check.EvaluateAsync(Context(fresh: Array.Empty<InstanceStatus>()));
        Assert.Equal(PreflightStatus.Red, empty.Status);
    }

    // ---------- target_known, presence_off, target_confirmed ----------

    [Fact]
    public async Task TargetKnown_GreenForAManifestService_RedOtherwise()
    {
        var check = new TargetKnownCheck();

        Assert.Equal(PreflightStatus.Green, (await check.EvaluateAsync(Context(target: Service()))).Status);
        var red = await check.EvaluateAsync(Context(Request(target: "svc-missing")));
        Assert.Equal(PreflightStatus.Red, red.Status);
        Assert.Contains("svc-missing", red.Message);
    }

    [Theory]
    [InlineData(null, PreflightStatus.Green)]
    [InlineData(false, PreflightStatus.Green)]
    [InlineData(true, PreflightStatus.Red)]
    public async Task PresenceOff_ReadsTheTargetsRealtimePresence(bool? presence, string status)
    {
        var result = await new PresenceOffCheck().EvaluateAsync(Context(target: Service(presence)));

        Assert.Equal(status, result.Status);
    }

    [Fact]
    public async Task PresenceOff_Amber_WhenTheTargetIsUnknown()
    {
        Assert.Equal(PreflightStatus.Amber, (await new PresenceOffCheck().EvaluateAsync(Context())).Status);
    }

    [Theory]
    [InlineData("svc-a", "svc-a", PreflightStatus.Green)]
    [InlineData("svc-a", "SVC-A", PreflightStatus.Red)]
    [InlineData("svc-a", null, PreflightStatus.Red)]
    [InlineData("", "", PreflightStatus.Red)]
    public async Task TargetConfirmed_RequiresTheExactTargetName(string target, string? confirm, string status)
    {
        var result = await new TargetConfirmedCheck().EvaluateAsync(Context(Request(target: target, confirm: confirm)));

        Assert.Equal(status, result.Status);
    }

    // ---------- scenario_validated ----------

    [Fact]
    public async Task ScenarioValidated_GreenWhenTheLastValidationRanTheCurrentVersion()
    {
        var scenario = LoadTestHost.Scenario("tracker");
        scenario.ScriptVersion = 3;
        scenario.LastValidationRunId = 12;
        var check = new ScenarioValidatedCheck();

        var green = await check.EvaluateAsync(Context(scenario: scenario,
            validationRun: LoadTestHost.Run(12, LoadRunStatus.Succeeded, scenarioVersion: 3, validation: true)));
        Assert.Equal(PreflightStatus.Green, green.Status);

        var older = await check.EvaluateAsync(Context(scenario: scenario,
            validationRun: LoadTestHost.Run(12, LoadRunStatus.Succeeded, scenarioVersion: 2, validation: true)));
        Assert.Equal(PreflightStatus.Red, older.Status);

        scenario.LastValidationRunId = null;
        var never = await check.EvaluateAsync(Context(scenario: scenario));
        Assert.Equal(PreflightStatus.Red, never.Status);
        Assert.Contains("Validate first", never.Fix);

        var validation = await check.EvaluateAsync(Context(Request(validation: true), scenario: scenario));
        Assert.Equal(PreflightStatus.Green, validation.Status);

        Assert.Equal(PreflightStatus.Red, (await check.EvaluateAsync(Context())).Status);
    }

    // ---------- the orchestrator ----------

    private sealed class ThrowingCheck : ILoadPreflightCheck
    {
        public string Name => "boom";

        public Task<PreflightCheckResult> EvaluateAsync(LoadPreflightContext context, CancellationToken ct = default) =>
            throw new InvalidOperationException("database unreachable");
    }

    private sealed class CapturingCheck : ILoadPreflightCheck
    {
        public LoadPreflightContext? Seen { get; private set; }

        public string Name => "capture";

        public Task<PreflightCheckResult> EvaluateAsync(LoadPreflightContext context, CancellationToken ct = default)
        {
            Seen = context;
            return Task.FromResult(new PreflightCheckResult(Name, PreflightStatus.Green, "ok", null));
        }
    }

    [Fact]
    public async Task LoadPreflight_BuildsTheContext_AndAThrowingCheckAnswersRed()
    {
        var store = new FakeLoadStore();
        var scenario = LoadTestHost.Scenario("tracker");
        scenario.LastValidationRunId = 12;
        store.Seed(scenario);
        store.Seed(LoadTestHost.Run(12, LoadRunStatus.Succeeded, validation: true));
        var manifests = new InMemoryManifestStore();
        await manifests.UpsertAsync(Service());
        var instances = new FakeInstanceStatusStore();
        instances.Seed(LoadTestHost.Instance("i-0fresh", Now.AddSeconds(-30)));
        instances.Seed(LoadTestHost.Instance("i-0stale", Now.AddSeconds(-91)));

        var capture = new CapturingCheck();
        var preflight = new LoadPreflight(
            new ILoadPreflightCheck[] { new ThrowingCheck(), capture },
            store,
            manifests,
            instances,
            new LoadOptions { GeneratorGroup = Group },
            new ReconcilerOptions { InstanceStaleThreshold = TimeSpan.FromSeconds(90) },
            new FixedClock());

        var results = await preflight.EvaluateAsync(Request());

        Assert.Equal(new[] { "boom", "capture" }, results.Select(r => r.Name));
        Assert.Equal(PreflightStatus.Red, results[0].Status);
        Assert.Contains("database unreachable", results[0].Message);

        var seen = capture.Seen!;
        Assert.Equal(Now, seen.Now);
        Assert.Equal("tracker", seen.Scenario?.Name);
        Assert.Equal(12, seen.ValidationRun?.Id);
        Assert.Equal(LoadTestHost.Target, seen.TargetService?.Name);
        Assert.Equal("i-0fresh", Assert.Single(seen.FreshInstances).InstanceId);
        Assert.Equal("i-0stale", Assert.Single(seen.StaleInstances).InstanceId);
    }
}
