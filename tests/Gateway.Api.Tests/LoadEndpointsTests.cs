using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gateway.Api.Data;
using Gateway.Api.Management;

namespace Gateway.Api.Tests;

/// <summary>
/// Integration coverage for the Cognito-gated <c>/mgmt/load</c> routes (docs/load.md
/// section 8): scenario upsert rules (create vs update, script versions, validation
/// reset, defaults), run Start validation and every 409, the run reads, stop, verdict
/// and delete rules, the stats reads (raw, downsampled, CSV), the generator log, and
/// the <c>ops:load</c> events.
/// </summary>
public class LoadEndpointsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly DateTimeOffset Now = new(2026, 11, 5, 2, 0, 0, TimeSpan.Zero);

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset At = Now;
        public override DateTimeOffset GetUtcNow() => At;
    }

    private static JsonElement Data(JobTestHost.RecordingPublisher.Published p) =>
        JsonSerializer.SerializeToElement(p.Data, Web);

    private static IEnumerable<JobTestHost.RecordingPublisher.Published> OnLoad(LoadTestHost host, string @event) =>
        host.Events.All.Where(p => p.Channel == LoadEndpoints.OpsLoadChannel && p.Event == @event);

    private static object CreateBody(string script = "export default function () {}") => new
    {
        repository = "example-org/example-app",
        commitSha = "0123abc",
        script,
        maxVusPerGenerator = 500,
        paramsDefault = new { channel = "tracker", baseUrl = "https://app.example.com" },
    };

    private static Dictionary<string, object?> StartBody(int generators = 2, int vus = 100, int ramp = 60, int hold = 120) => new()
    {
        ["scenario"] = "tracker",
        ["target"] = LoadTestHost.Target,
        ["confirm"] = LoadTestHost.Target,
        ["generators"] = generators,
        ["vusPerGenerator"] = vus,
        ["rampSeconds"] = ramp,
        ["holdSeconds"] = hold,
    };

    private static async Task<LoadTestHost> ReadyHostAsync(TestClock? clock = null)
    {
        var host = new LoadTestHost(clock);
        await host.SeedTargetAsync(heartbeatAt: clock?.At);
        host.SeedReadyScenario();
        return host;
    }

    // ---------- scenarios ----------

    [Fact]
    public async Task PutScenario_Creates_201_VersionOne_WithDefaults_AndHistory()
    {
        await using var host = new LoadTestHost();
        var response = await host.Admin().PutAsJsonAsync("/mgmt/load/scenarios/tracker", CreateBody());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tracker", body.GetProperty("name").GetString());
        Assert.Equal("example-org/example-app", body.GetProperty("repository").GetString());
        Assert.Equal("0123abc", body.GetProperty("commitSha").GetString());
        Assert.Equal(1, body.GetProperty("scriptVersion").GetInt32());
        Assert.Equal(JobEndpoints.FallbackImage, body.GetProperty("image").GetString());
        Assert.Equal(JobEndpoints.FallbackTag, body.GetProperty("tag").GetString());
        Assert.Equal("tracker", body.GetProperty("paramsDefault").GetProperty("channel").GetString());
        Assert.Equal(500, body.GetProperty("maxVusPerGenerator").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lastValidationRunId").ValueKind);
        Assert.False(body.GetProperty("validated").GetBoolean());
        Assert.Equal("alice-admin", body.GetProperty("updatedBy").GetString());
        Assert.Equal("export default function () {}", body.GetProperty("script").GetString());

        var version = Assert.Single(host.Store.Versions);
        Assert.Equal(("tracker", 1), (version.Scenario, version.Version));
        Assert.Equal(JobEndpoints.Sha256Hex("export default function () {}"), version.Sha256);

        var published = Data(Assert.Single(OnLoad(host, "scenario")));
        Assert.Equal("tracker", published.GetProperty("name").GetString());
        Assert.Equal("upserted", published.GetProperty("action").GetString());
        Assert.Equal("alice-admin", published.GetProperty("by").GetString());
    }

    [Fact]
    public async Task PutScenario_WithoutImageOrTag_UsesConfiguredDefaults()
    {
        await using var host = new LoadTestHost(configure: o =>
        {
            o.DefaultImage = "registry.example.com/runner";
            o.DefaultTag = "v7";
        });

        var body = await (await host.Admin().PutAsJsonAsync("/mgmt/load/scenarios/tracker", CreateBody()))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("registry.example.com/runner", body.GetProperty("image").GetString());
        Assert.Equal("v7", body.GetProperty("tag").GetString());
    }

    [Fact]
    public async Task PutScenario_Update_SameScript_200_KeepsVersionAndValidation()
    {
        await using var host = new LoadTestHost();
        host.SeedReadyScenario();

        var response = await host.Admin().PutAsJsonAsync("/mgmt/load/scenarios/tracker", new { maxVusPerGenerator = 100, commitSha = "" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("scriptVersion").GetInt32());
        Assert.Equal(100, body.GetProperty("maxVusPerGenerator").GetInt32());
        Assert.Equal(900, body.GetProperty("lastValidationRunId").GetInt64());
        Assert.True(body.GetProperty("validated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("commitSha").ValueKind);
        Assert.Equal("example-org/example-app", body.GetProperty("repository").GetString());
        Assert.Single(host.Store.Versions);
    }

    [Fact]
    public async Task PutScenario_ChangedScript_BumpsVersion_WritesHistory_ClearsValidation()
    {
        await using var host = new LoadTestHost();
        host.SeedReadyScenario();

        var body = await (await host.Admin().PutAsJsonAsync("/mgmt/load/scenarios/tracker", new { script = "export default function () { sleep(1) }" }))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, body.GetProperty("scriptVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lastValidationRunId").ValueKind);
        Assert.False(body.GetProperty("validated").GetBoolean());
        Assert.Equal(new[] { 1, 2 }, host.Store.Versions.Select(v => v.Version).OrderBy(v => v));

        var versions = await host.Admin().GetFromJsonAsync<JsonElement>("/mgmt/load/scenarios/tracker/script/versions");
        Assert.Equal(new[] { 2, 1 }, versions.EnumerateArray().Select(v => v.GetProperty("version").GetInt32()));
        var v2 = await host.Admin().GetFromJsonAsync<JsonElement>("/mgmt/load/scenarios/tracker/script/versions/2");
        Assert.Equal("export default function () { sleep(1) }", v2.GetProperty("script").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await host.Admin().GetAsync("/mgmt/load/scenarios/tracker/script/versions/7")).StatusCode);
    }

    public static TheoryData<string, string, string> InvalidScenarios => new()
    {
        { "Bad_Name", """{"repository":"example-org/app","script":"x","maxVusPerGenerator":1}""", "name" },
        { "fresh", """{"script":"x","maxVusPerGenerator":1}""", "repository" },
        { "fresh", """{"repository":"example-org/app","maxVusPerGenerator":1}""", "script" },
        { "fresh", """{"repository":"example-org/app","script":"x"}""", "maxVusPerGenerator" },
        { "fresh", """{"repository":"no-slash","script":"x","maxVusPerGenerator":1}""", "repository" },
        { "fresh", """{"repository":"example-org/app","script":"x","maxVusPerGenerator":0}""", "maxVusPerGenerator" },
        { "fresh", """{"repository":"example-org/app","script":"x","maxVusPerGenerator":100001}""", "maxVusPerGenerator" },
        { "fresh", """{"repository":"example-org/app","script":"   ","maxVusPerGenerator":1}""", "script" },
        { "fresh", """{"repository":"example-org/app","script":"x","maxVusPerGenerator":1,"paramsDefault":{"n":1}}""", "paramsDefault" },
        { "fresh", """{"repository":"example-org/app","script":"x","maxVusPerGenerator":1,"paramsDefault":["a"]}""", "paramsDefault" },
        { "fresh", """{"repository":"example-org/app","script":"x","maxVusPerGenerator":1,"paramsDefault":{"a":{"b":"c"}}}""", "paramsDefault" },
        { "fresh", """{"repository":"example-org/app","script":"x","maxVusPerGenerator":1,"image":" "}""", "image" },
        { "fresh", """{"repository":"example-org/app","script":"x","maxVusPerGenerator":1,"tag":""}""", "tag" },
        { "fresh", """{"repository":"example-org/app","script":"x","maxVusPerGenerator":1,"commitSha":"not-hex"}""", "commitSha" },
    };

    [Theory]
    [MemberData(nameof(InvalidScenarios))]
    public async Task PutScenario_Invalid_400_NamesTheField(string name, string json, string field)
    {
        await using var host = new LoadTestHost();
        var response = await host.Admin().PutAsync($"/mgmt/load/scenarios/{name}", LoadTestHost.Json(json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(field, body.GetProperty("field").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("error").GetString()));
        Assert.Empty(host.Store.Scenarios);
    }

    [Fact]
    public async Task PutScenario_ScriptOverCap_400()
    {
        await using var host = new LoadTestHost();
        var response = await host.Admin().PutAsJsonAsync("/mgmt/load/scenarios/tracker", CreateBody(new string('x', LoadEndpoints.MaxScriptBytes + 1)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("script", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
    }

    [Fact]
    public async Task ListScenarios_NeverCarriesTheScript_GetDoes()
    {
        await using var host = new LoadTestHost();
        host.SeedReadyScenario("tracker");
        host.Store.Seed(LoadTestHost.Scenario("alpha"));

        var list = await host.Admin().GetFromJsonAsync<JsonElement>("/mgmt/load/scenarios");
        Assert.Equal(new[] { "alpha", "tracker" }, list.EnumerateArray().Select(s => s.GetProperty("name").GetString()));
        Assert.All(list.EnumerateArray(), s => Assert.False(s.TryGetProperty("script", out _)));
        Assert.True(list[1].GetProperty("validated").GetBoolean());

        var one = await host.Admin().GetFromJsonAsync<JsonElement>("/mgmt/load/scenarios/tracker");
        Assert.Equal("export default function () {}", one.GetProperty("script").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await host.Admin().GetAsync("/mgmt/load/scenarios/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Admin().GetAsync("/mgmt/load/scenarios/nope/script/versions")).StatusCode);
    }

    [Fact]
    public async Task DeleteScenario_409_WhileARunOfItIsActive_204_Otherwise_HistoryStays()
    {
        await using var host = new LoadTestHost();
        host.SeedReadyScenario();
        host.Store.Seed(LoadTestHost.Run(5, LoadRunStatus.Holding));

        var blocked = await host.Admin().DeleteAsync("/mgmt/load/scenarios/tracker");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("run_active", (await blocked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        host.Store.Run(5).Status = LoadRunStatus.Stopped;
        Assert.Equal(HttpStatusCode.NoContent, (await host.Admin().DeleteAsync("/mgmt/load/scenarios/tracker")).StatusCode);
        Assert.Empty(host.Store.Scenarios);
        Assert.Single(host.Store.Versions);
        Assert.Equal(2, host.Store.Runs.Count);
        Assert.Equal("deleted", Data(Assert.Single(OnLoad(host, "scenario"))).GetProperty("action").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await host.Admin().DeleteAsync("/mgmt/load/scenarios/tracker")).StatusCode);
    }

    [Fact]
    public async Task PutScenario_RecreateAfterDelete_ContinuesAfterTheKeptHistory()
    {
        await using var host = new LoadTestHost();
        host.SeedReadyScenario();
        await host.Admin().DeleteAsync("/mgmt/load/scenarios/tracker");

        var response = await host.Admin().PutAsJsonAsync("/mgmt/load/scenarios/tracker", CreateBody("export default function () { /* v2 */ }"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scriptVersion").GetInt32());
        Assert.Equal(new[] { 1, 2 }, host.Store.Versions.Select(v => v.Version).OrderBy(v => v));
    }

    // ---------- start ----------

    [Fact]
    public async Task Start_202_InsertsAQueuedRun_WithHashedToken_HardStop_MergedParams_AndPublishes()
    {
        var clock = new TestClock();
        await using var host = await ReadyHostAsync(clock);
        var body = StartBody();
        body["params"] = new { baseUrl = "https://dev.example.com" };
        body["fleetSize"] = 3;
        body["restoreFleet"] = false;

        var response = await host.Admin().PostAsJsonAsync("/mgmt/load/runs", body);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var runId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("runId").GetInt64();
        var run = host.Store.Run(runId);
        Assert.Equal(LoadRunStatus.Queued, run.Status);
        Assert.Equal(("tracker", 1, LoadTestHost.Target), (run.Scenario, run.ScenarioVersion, run.Target));
        Assert.Equal((2, 100, 60, 120), (run.Generators, run.VusPerGenerator, run.RampSeconds, run.HoldSeconds));
        Assert.Equal(3, run.FleetSizeRequested);
        Assert.False(run.RestoreFleet);
        Assert.False(run.Validation);
        Assert.Equal("alice-admin", run.Actor);
        Assert.Equal(Now, run.QueuedAt);
        Assert.Null(run.StartedAt);
        Assert.Equal(Now.AddSeconds(180).AddMinutes(10), run.HardStopAt);
        Assert.Matches("^[0-9a-f]{64}$", run.TokenHash);
        Assert.Equal((0, "{}", "{}"), (run.CheckedIn, run.Checkins, run.Peaks));

        var runParams = JsonDocument.Parse(run.Params).RootElement;
        Assert.Equal("tracker", runParams.GetProperty("channel").GetString());
        Assert.Equal("https://dev.example.com", runParams.GetProperty("baseUrl").GetString());

        var timeline = JsonDocument.Parse(run.Timeline).RootElement;
        Assert.Equal("queued", Assert.Single(timeline.EnumerateArray()).GetProperty("status").GetString());

        var published = Data(Assert.Single(OnLoad(host, "run")));
        Assert.Equal(runId, published.GetProperty("runId").GetInt64());
        Assert.Equal("queued", published.GetProperty("status").GetString());
        Assert.Equal("tracker", published.GetProperty("scenario").GetString());
        Assert.Equal(2, published.GetProperty("generators").GetInt32());
        Assert.Equal(100, published.GetProperty("vusPerGenerator").GetInt32());

        // The token is shown to nobody: not in the run view either.
        var view = await host.Admin().GetStringAsync($"/mgmt/load/runs/{runId}");
        Assert.DoesNotContain("token", view, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_HardStop_IsCappedAtFourHours()
    {
        var clock = new TestClock();
        await using var host = await ReadyHostAsync(clock);

        var response = await host.Admin().PostAsJsonAsync("/mgmt/load/runs", StartBody(ramp: 7200, hold: 10800));

        var runId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("runId").GetInt64();
        Assert.Equal(Now.AddHours(4), host.Store.Run(runId).HardStopAt);
    }

    [Fact]
    public async Task Start_Validation_ForcesOneGeneratorOneUserNoRamp_AndSkipsTheValidationCheck()
    {
        await using var host = new LoadTestHost(configure: o => o.ValidationHoldSeconds = 240);
        await host.SeedTargetAsync();
        host.Store.Seed(LoadTestHost.Scenario("tracker"), LoadTestHost.Version("tracker", 1, "export default function () {}"));

        var body = StartBody(generators: 2, vus: 400, ramp: 600, hold: 600);
        body["validation"] = true;
        var response = await host.Admin().PostAsJsonAsync("/mgmt/load/runs", body);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var run = host.Store.Run((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("runId").GetInt64());
        Assert.True(run.Validation);
        Assert.Equal((1, 1, 0, 240), (run.Generators, run.VusPerGenerator, run.RampSeconds, run.HoldSeconds));
        Assert.True(run.RestoreFleet);
    }

    public static TheoryData<string, string> InvalidStarts => new()
    {
        { """{"target":"svc-a","generators":1,"vusPerGenerator":1,"rampSeconds":0,"holdSeconds":0}""", "scenario" },
        { """{"scenario":"nope","target":"svc-a","generators":1,"vusPerGenerator":1,"rampSeconds":0,"holdSeconds":0}""", "scenario" },
        { """{"scenario":"tracker","generators":1,"vusPerGenerator":1,"rampSeconds":0,"holdSeconds":0}""", "target" },
        { """{"scenario":"tracker","target":"svc-a","vusPerGenerator":1,"rampSeconds":0,"holdSeconds":0}""", "generators" },
        { """{"scenario":"tracker","target":"svc-a","generators":0,"vusPerGenerator":1,"rampSeconds":0,"holdSeconds":0}""", "generators" },
        { """{"scenario":"tracker","target":"svc-a","generators":3,"vusPerGenerator":1,"rampSeconds":0,"holdSeconds":0}""", "generators" },
        { """{"scenario":"tracker","target":"svc-a","generators":1,"vusPerGenerator":0,"rampSeconds":0,"holdSeconds":0}""", "vusPerGenerator" },
        { """{"scenario":"tracker","target":"svc-a","generators":1,"vusPerGenerator":501,"rampSeconds":0,"holdSeconds":0}""", "vusPerGenerator" },
        { """{"scenario":"tracker","target":"svc-a","generators":1,"vusPerGenerator":1,"rampSeconds":-1,"holdSeconds":0}""", "rampSeconds" },
        { """{"scenario":"tracker","target":"svc-a","generators":1,"vusPerGenerator":1,"rampSeconds":7201,"holdSeconds":0}""", "rampSeconds" },
        { """{"scenario":"tracker","target":"svc-a","generators":1,"vusPerGenerator":1,"rampSeconds":0}""", "holdSeconds" },
        { """{"scenario":"tracker","target":"svc-a","generators":1,"vusPerGenerator":1,"rampSeconds":0,"holdSeconds":10801}""", "holdSeconds" },
        { """{"scenario":"tracker","target":"svc-a","generators":1,"vusPerGenerator":1,"rampSeconds":0,"holdSeconds":0,"params":{"a":1}}""", "params" },
        { """{"scenario":"tracker","target":"svc-a","generators":1,"vusPerGenerator":1,"rampSeconds":0,"holdSeconds":0,"fleetSize":0}""", "fleetSize" },
    };

    [Theory]
    [MemberData(nameof(InvalidStarts))]
    public async Task Start_Invalid_400_NamesTheField(string json, string field)
    {
        await using var host = await ReadyHostAsync();
        var response = await host.Admin().PostAsync("/mgmt/load/runs", LoadTestHost.Json(json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(field, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
        Assert.Single(host.Store.Runs);
    }

    [Fact]
    public async Task Start_503_LoadUnconfigured_WhenTheGeneratorGroupIsUnset()
    {
        await using var host = new LoadTestHost(configure: o => o.GeneratorGroup = null);
        await host.SeedTargetAsync();
        host.SeedReadyScenario();

        var response = await host.Admin().PostAsJsonAsync("/mgmt/load/runs", StartBody());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("load_unconfigured", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(LoadRunStatus.Queued)]
    [InlineData(LoadRunStatus.FleetUp)]
    [InlineData(LoadRunStatus.GeneratorsUp)]
    [InlineData(LoadRunStatus.Ramping)]
    [InlineData(LoadRunStatus.Holding)]
    [InlineData(LoadRunStatus.Draining)]
    public async Task Start_409_RunActive_WhileAnyRunIsNonTerminal(string status)
    {
        await using var host = await ReadyHostAsync();
        host.Store.Seed(LoadTestHost.Run(7, status, scenario: "other"));

        var response = await host.Admin().PostAsJsonAsync("/mgmt/load/runs", StartBody());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("run_active", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Empty(OnLoad(host, "run"));
    }

    [Fact]
    public async Task Start_409_PreflightFailed_CarriesEveryCheck()
    {
        await using var host = await ReadyHostAsync();
        var body = StartBody();
        body["confirm"] = "svc-b";

        var response = await host.Admin().PostAsJsonAsync("/mgmt/load/runs", body);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("preflight_failed", json.GetProperty("error").GetString());
        var checks = json.GetProperty("checks").EnumerateArray().ToDictionary(
            c => c.GetProperty("name").GetString()!, c => c.GetProperty("status").GetString());
        Assert.Equal("red", checks["target_confirmed"]);
        Assert.Equal("green", checks["target_known"]);
        Assert.Equal("amber", checks["fleet_class"]);
        Assert.Equal(8, checks.Count);
        Assert.Single(host.Store.Runs);
    }

    [Fact]
    public async Task Start_409_PreflightFailed_WhenTheScenarioIsNotValidated()
    {
        await using var host = new LoadTestHost();
        await host.SeedTargetAsync();
        host.Store.Seed(LoadTestHost.Scenario("tracker"), LoadTestHost.Version("tracker", 1, "export default function () {}"));

        var response = await host.Admin().PostAsJsonAsync("/mgmt/load/runs", StartBody());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var checks = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("checks");
        Assert.Contains(checks.EnumerateArray(), c =>
            c.GetProperty("name").GetString() == "scenario_validated" && c.GetProperty("status").GetString() == "red");
    }

    [Fact]
    public async Task Preflight_Get_AnswersEveryCheck_AndOk()
    {
        await using var host = await ReadyHostAsync();

        var json = await host.Admin().GetFromJsonAsync<JsonElement>(
            "/mgmt/load/preflight?scenario=tracker&target=svc-a&confirm=svc-a&generators=2&vus=100");

        Assert.True(json.GetProperty("ok").GetBoolean());
        var names = json.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Equal(new[]
        {
            "generator_group", "fleet_healthy", "target_known", "scenario_validated",
            "presence_off", "database_headroom", "fleet_class", "target_confirmed",
        }, names);
        Assert.All(json.GetProperty("checks").EnumerateArray(), c =>
        {
            Assert.True(c.TryGetProperty("message", out _));
            Assert.True(c.TryGetProperty("fix", out _));
        });

        var red = await host.Admin().GetFromJsonAsync<JsonElement>("/mgmt/load/preflight?scenario=tracker&target=svc-a");
        Assert.False(red.GetProperty("ok").GetBoolean());
    }

    // ---------- run reads ----------

    [Fact]
    public async Task ListRuns_NewestFirst_Paged_WithoutTimelineOrToken()
    {
        await using var host = new LoadTestHost();
        for (var id = 1; id <= 4; id++)
        {
            host.Store.Seed(LoadTestHost.Run(id, LoadRunStatus.Succeeded));
        }

        var all = await host.Admin().GetFromJsonAsync<JsonElement>("/mgmt/load/runs");
        Assert.Equal(new long[] { 4, 3, 2, 1 }, all.EnumerateArray().Select(r => r.GetProperty("id").GetInt64()));
        Assert.All(all.EnumerateArray(), r =>
        {
            Assert.False(r.TryGetProperty("timeline", out _));
            Assert.False(r.TryGetProperty("tokenHash", out _));
            Assert.True(r.TryGetProperty("peaks", out _));
        });

        var page = await host.Admin().GetFromJsonAsync<JsonElement>("/mgmt/load/runs?limit=2&before=4");
        Assert.Equal(new long[] { 3, 2 }, page.EnumerateArray().Select(r => r.GetProperty("id").GetInt64()));
    }

    [Fact]
    public async Task GetRun_CarriesTimelinePeaksAndCheckins_404WhenUnknown()
    {
        await using var host = new LoadTestHost();
        var run = LoadTestHost.Run(3, LoadRunStatus.Holding);
        run.Timeline = """[{"at":"2026-11-05T02:00:00+00:00","status":"queued","by":"alice","note":null}]""";
        run.Peaks = """{"event_lag_ms.p(99)":310}""";
        run.Checkins = """{"i-0placeholder":0}""";
        host.Store.Seed(run);

        var json = await host.Admin().GetFromJsonAsync<JsonElement>("/mgmt/load/runs/3");

        Assert.Equal("holding", json.GetProperty("status").GetString());
        Assert.Equal("queued", json.GetProperty("timeline")[0].GetProperty("status").GetString());
        Assert.Equal(310, json.GetProperty("peaks").GetProperty("event_lag_ms.p(99)").GetDouble());
        Assert.Equal(0, json.GetProperty("checkins").GetProperty("i-0placeholder").GetInt32());
        Assert.Equal("tracker", json.GetProperty("params").GetProperty("channel").GetString());
        Assert.False(json.TryGetProperty("tokenHash", out _));

        Assert.Equal(HttpStatusCode.NotFound, (await host.Admin().GetAsync("/mgmt/load/runs/99")).StatusCode);
    }

    // ---------- stop, verdict, delete ----------

    [Fact]
    public async Task Stop_202_StampsOnce_AddsATimelineEntry_AndPublishes()
    {
        var clock = new TestClock();
        await using var host = new LoadTestHost(clock);
        host.Store.Seed(LoadTestHost.Run(3, LoadRunStatus.Ramping));

        var response = await host.Admin().PostAsync("/mgmt/load/runs/3/stop", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Now, json.GetProperty("stopRequestedAt").GetDateTimeOffset());
        Assert.Equal("ramping", json.GetProperty("status").GetString());
        Assert.Equal("stop requested", json.GetProperty("timeline")[0].GetProperty("note").GetString());
        Assert.Equal("ramping", Data(Assert.Single(OnLoad(host, "run"))).GetProperty("status").GetString());

        // A second request keeps the first stamp.
        clock.At = Now.AddMinutes(1);
        Assert.Equal(HttpStatusCode.Accepted, (await host.Admin().PostAsync("/mgmt/load/runs/3/stop", null)).StatusCode);
        Assert.Equal(Now, host.Store.Run(3).StopRequestedAt);
        Assert.Single(JsonDocument.Parse(host.Store.Run(3).Timeline).RootElement.EnumerateArray());
    }

    [Fact]
    public async Task Stop_409_WhenTerminal_404_WhenUnknown()
    {
        await using var host = new LoadTestHost();
        host.Store.Seed(LoadTestHost.Run(3, LoadRunStatus.Expired));

        var response = await host.Admin().PostAsync("/mgmt/load/runs/3/stop", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("run_finished", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Null(host.Store.Run(3).StopRequestedAt);

        Assert.Equal(HttpStatusCode.NotFound, (await host.Admin().PostAsync("/mgmt/load/runs/9/stop", null)).StatusCode);
    }

    [Fact]
    public async Task Verdict_OnATerminalRun_SetsAndClears_409WhileActive_400WhenTooLong()
    {
        await using var host = new LoadTestHost();
        host.Store.Seed(LoadTestHost.Run(3, LoadRunStatus.Succeeded));
        host.Store.Seed(LoadTestHost.Run(4, LoadRunStatus.Holding));

        var set = await host.Admin().PutAsJsonAsync("/mgmt/load/runs/3/verdict", new { verdict = "held 200 users, p99 under 400 ms" });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal("held 200 users, p99 under 400 ms", (await set.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("verdict").GetString());

        Assert.Equal(HttpStatusCode.OK, (await host.Admin().PutAsJsonAsync("/mgmt/load/runs/3/verdict", new { verdict = "" })).StatusCode);
        Assert.Null(host.Store.Run(3).Verdict);

        var tooLong = await host.Admin().PutAsJsonAsync("/mgmt/load/runs/3/verdict", new { verdict = new string('v', 501) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("verdict", (await tooLong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await host.Admin().PutAsJsonAsync("/mgmt/load/runs/4/verdict", new { verdict = "early" })).StatusCode);
        Assert.Null(host.Store.Run(4).Verdict);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Admin().PutAsJsonAsync("/mgmt/load/runs/9/verdict", new { verdict = "x" })).StatusCode);
    }

    [Fact]
    public async Task DeleteRun_RemovesATerminalRunAndItsStats_409WhileActive()
    {
        await using var host = new LoadTestHost();
        host.Store.Seed(LoadTestHost.Run(3, LoadRunStatus.Failed));
        host.Store.Seed(LoadTestHost.Run(4, LoadRunStatus.Draining));
        await host.Store.AppendStatAsync(new LoadRunStat { RunId = 3, Source = "generator", Index = 0, At = Now, Body = "{}" });
        await host.Store.AppendStatAsync(new LoadRunStat { RunId = 4, Source = "generator", Index = 0, At = Now, Body = "{}" });

        Assert.Equal(HttpStatusCode.NoContent, (await host.Admin().DeleteAsync("/mgmt/load/runs/3")).StatusCode);
        Assert.DoesNotContain(host.Store.Runs, r => r.Id == 3);
        Assert.DoesNotContain(host.Store.Stats, s => s.RunId == 3);

        var active = await host.Admin().DeleteAsync("/mgmt/load/runs/4");
        Assert.Equal(HttpStatusCode.Conflict, active.StatusCode);
        Assert.Equal("run_active", (await active.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Single(host.Store.Stats);

        Assert.Equal(HttpStatusCode.NotFound, (await host.Admin().DeleteAsync("/mgmt/load/runs/3")).StatusCode);
    }

    // ---------- stats reads ----------

    private static async Task SeedStatsAsync(LoadTestHost host)
    {
        host.Store.Seed(LoadTestHost.Run(3, LoadRunStatus.Holding));
        var lines = new (int Index, int Second, int Vus, int P99, int Errors)[]
        {
            (0, 0, 100, 300, 1),
            (1, 2, 90, 500, 0),
            (0, 5, 110, 200, 3),
            (0, 12, 120, 250, 4),
        };
        foreach (var (index, second, vus, p99, errors) in lines)
        {
            var at = Now.AddSeconds(second);
            await host.Store.AppendStatAsync(new LoadRunStat
            {
                RunId = 3,
                Source = LoadStatSource.Generator,
                Index = index,
                At = at,
                Body = LoadTestHost.StatsLine(3, index, at, vus,
                    "{\"event_lag_ms\":{\"type\":\"trend\",\"p(99)\":" + p99
                    + "},\"ws_errors\":{\"type\":\"counter\",\"count\":" + errors + "}}"),
            });
        }

        await host.Store.AppendStatAsync(new LoadRunStat
        {
            RunId = 3,
            Source = LoadStatSource.Fleet,
            At = Now.AddSeconds(3),
            Body = """{"instances":[{"instanceId":"i-0placeholder","cpu":41.5}]}""",
        });
    }

    [Fact]
    public async Task Stats_Raw_FilteredBySourceIndexAndWindow()
    {
        await using var host = new LoadTestHost();
        await SeedStatsAsync(host);

        var all = await host.Admin().GetFromJsonAsync<JsonElement>("/mgmt/load/runs/3/stats");
        Assert.Equal(5, all.GetArrayLength());
        Assert.Equal("generator", all[0].GetProperty("source").GetString());
        Assert.Equal(300, all[0].GetProperty("body").GetProperty("metrics").GetProperty("event_lag_ms").GetProperty("p(99)").GetInt32());

        var fleet = await host.Admin().GetFromJsonAsync<JsonElement>("/mgmt/load/runs/3/stats?source=fleet");
        Assert.Equal(JsonValueKind.Null, Assert.Single(fleet.EnumerateArray()).GetProperty("index").ValueKind);

        var index0 = await host.Admin().GetFromJsonAsync<JsonElement>("/mgmt/load/runs/3/stats?source=generator&index=0");
        Assert.Equal(3, index0.GetArrayLength());

        var from = Uri.EscapeDataString(Now.AddSeconds(4).ToString("O"));
        var to = Uri.EscapeDataString(Now.AddSeconds(10).ToString("O"));
        var window = await host.Admin().GetFromJsonAsync<JsonElement>($"/mgmt/load/runs/3/stats?from={from}&to={to}");
        Assert.Equal(110, Assert.Single(window.EnumerateArray()).GetProperty("body").GetProperty("vus").GetInt32());
    }

    [Fact]
    public async Task Stats_Step_DownsamplesToTheMaximumOfEachLeafPerBucket()
    {
        await using var host = new LoadTestHost();
        await SeedStatsAsync(host);

        var json = await host.Admin().GetFromJsonAsync<JsonElement>("/mgmt/load/runs/3/stats?source=generator&index=0&step=10");

        // Seconds 0 and 5 share the first bucket; second 12 is alone in the next.
        Assert.Equal(2, json.GetArrayLength());
        var first = json[0];
        Assert.Equal(Now, first.GetProperty("at").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("id").ValueKind);
        Assert.Equal(110, first.GetProperty("body").GetProperty("vus").GetDouble());
        Assert.Equal(300, first.GetProperty("body").GetProperty("metrics").GetProperty("event_lag_ms").GetProperty("p(99)").GetDouble());
        Assert.Equal(3, first.GetProperty("body").GetProperty("metrics").GetProperty("ws_errors").GetProperty("count").GetDouble());
        Assert.Equal("trend", first.GetProperty("body").GetProperty("metrics").GetProperty("event_lag_ms").GetProperty("type").GetString());
        Assert.Equal(Now.AddSeconds(10), json[1].GetProperty("at").GetDateTimeOffset());
    }

    [Fact]
    public async Task StatsCsv_FlattensOneColumnPerNumericLeaf()
    {
        await using var host = new LoadTestHost();
        await SeedStatsAsync(host);

        var response = await host.Admin().GetAsync("/mgmt/load/runs/3/stats.csv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("load-run-3-stats.csv", response.Content.Headers.ContentDisposition?.ToString());
        var lines = (await response.Content.ReadAsStringAsync()).TrimEnd('\n').Split('\n');
        Assert.Equal(
            "at,source,index,instances.0.cpu,metrics.event_lag_ms.p(99),metrics.ws_errors.count,vus",
            lines[0]);
        Assert.Equal(6, lines.Length);
        Assert.Equal("2026-11-05T02:00:00.000Z,generator,0,,300,1,100", lines[1]);
        Assert.Equal("2026-11-05T02:00:03.000Z,fleet,,41.5,,,", lines[3]);

        var stepped = await host.Admin().GetStringAsync("/mgmt/load/runs/3/stats.csv?source=generator&index=0&step=10");
        Assert.Equal(3, stepped.TrimEnd('\n').Split('\n').Length);
    }

    [Fact]
    public async Task Stats_400_OnABadSourceOrStep_404_WhenTheRunIsUnknown()
    {
        await using var host = new LoadTestHost();
        host.Store.Seed(LoadTestHost.Run(3, LoadRunStatus.Holding));

        var source = await host.Admin().GetAsync("/mgmt/load/runs/3/stats?source=nope");
        Assert.Equal("source", (await source.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
        var step = await host.Admin().GetAsync("/mgmt/load/runs/3/stats.csv?step=0");
        Assert.Equal(HttpStatusCode.BadRequest, step.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Admin().GetAsync("/mgmt/load/runs/8/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Admin().GetAsync("/mgmt/load/runs/8/stats.csv")).StatusCode);
    }

    // ---------- log ----------

    [Fact]
    public async Task Log_ReadsTheGeneratorStream_FromTheLoadGroup()
    {
        await using var host = new LoadTestHost();
        host.Store.Seed(LoadTestHost.Run(7, LoadRunStatus.Holding));
        host.Factory.LogStore.SeedStream(
            CloudWatchLogStore.LoadLogGroup,
            "run-7-1",
            new LogLine(Now, "k6 started"),
            new LogLine(Now.AddSeconds(1), "running"));

        var json = await host.Admin().GetFromJsonAsync<JsonElement>("/mgmt/load/runs/7/log?index=1&tail=1");

        Assert.Equal("running", Assert.Single(json.EnumerateArray()).GetProperty("message").GetString());
        Assert.Equal(("/gateway/load", "run-7-1", 1), Assert.Single(host.Factory.LogStore.StreamQueries));

        Assert.Equal(HttpStatusCode.BadRequest, (await host.Admin().GetAsync("/mgmt/load/runs/7/log?index=2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Admin().GetAsync("/mgmt/load/runs/8/log")).StatusCode);
    }
}
