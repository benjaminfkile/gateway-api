using Gateway.Api.Data;
using Gateway.Api.Load;
using Gateway.Api.Manifest;
using Gateway.Api.Reconcile;
using Gateway.Api.Resources;
using Microsoft.Extensions.Configuration;

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
        int? fleet = null,
        bool validation = false,
        string target = LoadTestHost.Target,
        string? confirm = LoadTestHost.Target) =>
        new("tracker", target, confirm, generators, vus, fleet, validation);

    private static ServiceManifest Service(bool? presence = null) => new()
    {
        Name = LoadTestHost.Target,
        Image = "registry.example.com/svc-a",
        Tag = "v1",
        Port = 8080,
        RealtimePresence = presence,
        UpdatedBy = "seed",
    };

    private static ResourcesOptions Config(params (string Key, string Value)[] pairs) =>
        ResourcesOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build());

    private static FleetGroupResolver Groups(params (string Key, string Value)[] pairs) =>
        new(Config(pairs), new FakeFleetGroupSource(), new FixedClock());

    private static FakeFleetScaler Scaler(FleetGroupSize? generators = null)
    {
        var scaler = new FakeFleetScaler();
        scaler.Groups[Group] = generators ?? new FleetGroupSize(0, 0, 2, 0);
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
    [InlineData(0, 1, 2, 0, 1, "desired 1")]
    [InlineData(0, 0, 2, 1, 1, "1 in service")]
    [InlineData(0, 0, 1, 0, 2, "max is 1")]
    public async Task GeneratorGroup_Red_WhenAPreviousRunLeftInstances_OrTheGroupIsTooSmall(
        int min, int desired, int max, int inService, int generators, string says)
    {
        var check = new GeneratorGroupCheck(Scaler(new FleetGroupSize(min, desired, max, inService)));

        var result = await check.EvaluateAsync(Context(Request(generators: generators)));

        Assert.Equal(PreflightStatus.Red, result.Status);
        Assert.Contains(says, result.Message);
        Assert.NotNull(result.Fix);
    }

    [Fact]
    public async Task GeneratorGroup_AValidationRunCountsAsOneGenerator()
    {
        var check = new GeneratorGroupCheck(Scaler(new FleetGroupSize(0, 0, 1, 0)));

        var result = await check.EvaluateAsync(Context(Request(generators: 2, validation: true)));

        Assert.Equal(PreflightStatus.Green, result.Status);
    }

    [Fact]
    public async Task GeneratorGroup_Red_WhenUnset_Unknown_OrUnreadable()
    {
        var unset = await new GeneratorGroupCheck(Scaler()).EvaluateAsync(Context(options: new LoadOptions()));
        Assert.Equal(PreflightStatus.Red, unset.Status);
        Assert.Contains("Load:GeneratorGroup", unset.Message);

        var missing = await new GeneratorGroupCheck(new FakeFleetScaler()).EvaluateAsync(Context());
        Assert.Equal(PreflightStatus.Red, missing.Status);
        Assert.Contains("does not exist", missing.Message);

        var nullScaler = await new GeneratorGroupCheck(new NullFleetScaler()).EvaluateAsync(Context());
        Assert.Equal(PreflightStatus.Red, nullScaler.Status);
        Assert.Contains(NullFleetScaler.NotConfiguredMessage, nullScaler.Message);
    }

    // ---------- fleet_healthy ----------

    [Fact]
    public async Task FleetHealthy_Green_WhenEveryInstanceHeartbeatsAndRunsTheTarget()
    {
        var check = new FleetHealthyCheck(new FakeFleetScaler(), Groups());

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
        var check = new FleetHealthyCheck(new FakeFleetScaler(), Groups());

        var result = await check.EvaluateAsync(Context(stale: new[] { LoadTestHost.Instance("i-0stale", Now.AddMinutes(-5)) }));

        Assert.Equal(PreflightStatus.Red, result.Status);
        Assert.Contains("i-0stale", result.Message);
    }

    [Fact]
    public async Task FleetHealthy_Red_WhenTheTargetIsNotRunningOnAnInstance_OrNoInstanceHeartbeats()
    {
        var check = new FleetHealthyCheck(new FakeFleetScaler(), Groups());

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

    [Fact]
    public async Task FleetHealthy_Red_WhenTheFleetGroupHasMoreInServiceThanHeartbeat()
    {
        var scaler = new FakeFleetScaler();
        scaler.Groups["fleet-placeholder"] = new FleetGroupSize(2, 3, 4, 3);
        var check = new FleetHealthyCheck(scaler, Groups((ResourcesOptions.FleetGroupKey, "fleet-placeholder")));

        var result = await check.EvaluateAsync(Context());

        Assert.Equal(PreflightStatus.Red, result.Status);
        Assert.Contains("3 instances in service but 1 heartbeat", result.Message);

        // With no scaler the heartbeats are the whole check.
        var unreadable = new FleetHealthyCheck(new NullFleetScaler(), Groups((ResourcesOptions.FleetGroupKey, "fleet-placeholder")));
        Assert.Equal(PreflightStatus.Green, (await unreadable.EvaluateAsync(Context())).Status);
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

    // ---------- database_headroom ----------

    [Theory]
    // No fleet change: only the margin of 10 is needed; free 50.
    [InlineData(100, 50, 2, null, PreflightStatus.Green)]
    // No fleet change but free 5 against the margin of 10.
    [InlineData(100, 95, 2, null, PreflightStatus.Red)]
    // A requested fleet of 3 adds one node: 1 x 20 + 10 = 30 against free 25.
    [InlineData(100, 75, 2, 3, PreflightStatus.Red)]
    // The same request against free 30: exactly enough.
    [InlineData(100, 70, 2, 3, PreflightStatus.Green)]
    // A requested fleet smaller than the live one adds nothing.
    [InlineData(100, 95, 2, 1, PreflightStatus.Red)]
    // No instance heartbeats and no request: only the margin, free 30.
    [InlineData(40, 10, 0, null, PreflightStatus.Green)]
    public async Task DatabaseHeadroom_Arithmetic(int max, int inUse, int nodes, int? fleet, string status)
    {
        var headroom = new FakeDatabaseHeadroom { Connections = new DatabaseConnections(max, inUse) };
        var fresh = Enumerable.Range(0, nodes).Select(i => LoadTestHost.Instance($"i-0{i}", Now)).ToList();

        var result = await new DatabaseHeadroomCheck(headroom).EvaluateAsync(Context(Request(fleet: fleet), fresh: fresh));

        Assert.Equal(status, result.Status);
        Assert.Contains($"max_connections {max}, in use {inUse}, free {max - inUse}", result.Message);
        Assert.Equal(status == PreflightStatus.Red, result.Fix?.Contains("database card") == true);
    }

    [Fact]
    public async Task DatabaseHeadroom_UsesTheConfiguredPerNodeAndMargin_AmberWithoutADatabase()
    {
        var headroom = new FakeDatabaseHeadroom { Connections = new DatabaseConnections(100, 0) };
        var options = new LoadOptions { GeneratorGroup = Group, DbConnectionsPerNode = 45, DbConnectionMargin = 11 };

        var result = await new DatabaseHeadroomCheck(headroom).EvaluateAsync(Context(
            Request(fleet: 4),
            fresh: new[] { LoadTestHost.Instance("i-0a", Now), LoadTestHost.Instance("i-0b", Now) },
            options: options));
        Assert.Equal(PreflightStatus.Red, result.Status);
        Assert.Contains("2 live nodes, 2 added x 45 + 11 needs 101", result.Message);

        headroom.Connections = null;
        Assert.Equal(PreflightStatus.Amber, (await new DatabaseHeadroomCheck(headroom).EvaluateAsync(Context())).Status);
    }

    // ---------- fleet_class ----------

    [Theory]
    [InlineData("t4g.small", PreflightStatus.Amber)]
    [InlineData("t3a.medium", PreflightStatus.Amber)]
    [InlineData("t2.micro", PreflightStatus.Amber)]
    [InlineData("m7g.large", PreflightStatus.Green)]
    [InlineData("c7g.xlarge", PreflightStatus.Green)]
    public async Task FleetClass_AmberForABurstableFamily_NeverRed(string instanceType, string status)
    {
        var scaler = new FakeFleetScaler();
        scaler.Templates["fleet-template-placeholder"] = new FleetLaunchTemplate("lt-placeholder", 4, instanceType);
        var check = new FleetClassCheck(scaler, Config((ResourcesOptions.FleetTemplateKey, "fleet-template-placeholder")));

        var result = await check.EvaluateAsync(Context());

        Assert.Equal(status, result.Status);
        Assert.Contains(instanceType, result.Message);
    }

    [Fact]
    public async Task FleetClass_AmberUnknown_WithTheNullScaler_OrNoTemplate()
    {
        var configured = Config((ResourcesOptions.FleetTemplateKey, "fleet-template-placeholder"));

        var nullScaler = await new FleetClassCheck(new NullFleetScaler(), configured).EvaluateAsync(Context());
        Assert.Equal(PreflightStatus.Amber, nullScaler.Status);
        Assert.StartsWith("Unknown", nullScaler.Message);

        var unset = await new FleetClassCheck(new FakeFleetScaler(), Config()).EvaluateAsync(Context());
        Assert.Equal(PreflightStatus.Amber, unset.Status);
        Assert.StartsWith("Unknown", unset.Message);

        var missing = await new FleetClassCheck(new FakeFleetScaler(), configured).EvaluateAsync(Context());
        Assert.Equal(PreflightStatus.Amber, missing.Status);
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
