using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gateway.Api.Data;
using Gateway.Api.Load;
using Gateway.Api.Management;

namespace Gateway.Api.Tests;

/// <summary>
/// The two run-token endpoints the generators call (docs/load.md sections 4, 5 and 8):
/// the token gate (a wrong token and an unknown run answer the same 401), the 64 KiB
/// body cap, the per-address rate limit, check-in index assignment (in order, under
/// concurrency, repeated, full, not accepting), and stats intake (stored, peaks,
/// the merged aggregate on <c>ops:load</c>, continue or stop, 409 when terminal).
/// </summary>
public class LoadTokenEndpointsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly DateTimeOffset At = new(2026, 11, 5, 2, 14, 5, TimeSpan.Zero);

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = At;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static LoadTestHost HostWithRun(string status, int generators = 2)
    {
        var host = new LoadTestHost();
        host.Store.Seed(LoadTestHost.Scenario("tracker", script: "export default function () { /* v1 */ }"),
            LoadTestHost.Version("tracker", 1, "export default function () { /* v1 */ }"));
        host.Store.Seed(LoadTestHost.Run(41, status, generators: generators));
        return host;
    }

    private static Task<HttpResponseMessage> CheckInAsync(HttpClient client, string instanceId, long runId = 41) =>
        client.PostAsJsonAsync($"/mgmt/load/runs/{runId}/checkin", new { instanceId });

    private static Task<HttpResponseMessage> StatsAsync(HttpClient client, string line, long runId = 41) =>
        client.PostAsync($"/mgmt/load/runs/{runId}/stats", LoadTestHost.Json(line));

    private static async Task<string?> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    // ---------- the token gate ----------

    [Fact]
    public async Task WrongToken_UnknownRun_AndNoToken_AllAnswerTheSame401()
    {
        await using var host = HostWithRun(LoadRunStatus.GeneratorsUp);
        var line = LoadTestHost.StatsLine(41, 0, At, 1, "{}");

        var wrong = await CheckInAsync(host.Generator("not-the-token"), "i-0a");
        var unknown = await CheckInAsync(host.Generator(), "i-0a", runId: 99);
        var none = await CheckInAsync(host.Generator(null), "i-0a");
        var statsWrong = await StatsAsync(host.Generator("not-the-token"), line);
        var statsUnknown = await StatsAsync(host.Generator(), LoadTestHost.StatsLine(99, 0, At, 1, "{}"), runId: 99);

        var bodies = new List<string>();
        foreach (var response in new[] { wrong, unknown, none, statsWrong, statsUnknown })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        Assert.Single(bodies.Distinct());
        Assert.Equal(0, host.Store.Run(41).CheckedIn);
        Assert.Empty(host.Store.Stats);
    }

    [Fact]
    public void TokenHash_IsSha256Hex_AndTheComparisonMatchesOnlyTheToken()
    {
        var token = LoadEndpoints.MintToken();
        Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
        Assert.NotEqual(token, LoadEndpoints.MintToken());

        var hash = LoadEndpoints.TokenHash(token);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.True(LoadEndpoints.TokenMatches(token, hash));
        Assert.False(LoadEndpoints.TokenMatches(token + "x", hash));
        Assert.False(LoadEndpoints.TokenMatches("", LoadEndpoints.TokenHash("")));
        Assert.False(LoadEndpoints.TokenMatches(token, "not-hex"));
    }

    [Fact]
    public async Task BodyOverTheCap_413_OnBothEndpoints()
    {
        await using var host = HostWithRun(LoadRunStatus.GeneratorsUp);
        var big = "{\"instanceId\":\"" + new string('x', LoadEndpoints.MaxTokenBodyBytes) + "\"}";

        var checkin = await host.Generator().PostAsync("/mgmt/load/runs/41/checkin", LoadTestHost.Json(big));
        var stats = await host.Generator().PostAsync("/mgmt/load/runs/41/stats", LoadTestHost.Json(big));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, checkin.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, stats.StatusCode);
        Assert.Equal("body_too_large", await ErrorAsync(stats));
        Assert.Equal(0, host.Store.Run(41).CheckedIn);
    }

    [Fact]
    public async Task RateLimit_FivePerSecondWithABurst_PerSourceAddress()
    {
        var clock = new TestClock();
        await using var host = new LoadTestHost(limiter: new LoadTokenRateLimiter(clock: clock));
        host.Store.Seed(LoadTestHost.Run(41, LoadRunStatus.Holding));

        async Task<HttpStatusCode> PostAsync(string address)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mgmt/load/runs/41/stats")
            {
                Content = LoadTestHost.Json(LoadTestHost.StatsLine(41, 0, At, 1, "{}")),
            };
            request.Headers.Add("X-Forwarded-For", address);
            return (await host.Generator().SendAsync(request)).StatusCode;
        }

        for (var i = 0; i < LoadTokenRateLimiter.DefaultBurst; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await PostAsync("198.51.100.7"));
        }

        using (var over = new HttpRequestMessage(HttpMethod.Post, "/mgmt/load/runs/41/checkin"))
        {
            over.Headers.Add("X-Forwarded-For", "198.51.100.7");
            over.Content = LoadTestHost.Json("{\"instanceId\":\"i-0a\"}");
            var response = await host.Generator().SendAsync(over);
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.True(response.Headers.RetryAfter?.Delta >= TimeSpan.FromSeconds(1));
        }

        // Another address has its own bucket; the last forwarded hop is the key.
        Assert.Equal(HttpStatusCode.OK, await PostAsync("198.51.100.7, 203.0.113.9"));

        // One second refills five tokens.
        clock.Now = At.AddSeconds(1);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await PostAsync("198.51.100.7"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await PostAsync("198.51.100.7"));
    }

    // ---------- check-in ----------

    [Fact]
    public async Task CheckIn_AssignsIndexesInOrder_ARepeatGetsTheSameIndex_ThenRunFull()
    {
        await using var host = HostWithRun(LoadRunStatus.GeneratorsUp);
        var client = host.Generator();

        var first = await CheckInAsync(client, "i-0a");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("index").GetInt32());
        Assert.Equal(2, body.GetProperty("count").GetInt32());
        Assert.Equal(100, body.GetProperty("vus").GetInt32());
        Assert.Equal(60, body.GetProperty("rampSeconds").GetInt32());
        Assert.Equal(120, body.GetProperty("holdSeconds").GetInt32());
        Assert.Equal("tracker", body.GetProperty("scenario").GetString());
        Assert.Equal(LoadTestHost.Target, body.GetProperty("target").GetString());
        Assert.Equal("export default function () { /* v1 */ }", body.GetProperty("script").GetString());
        Assert.Equal("tracker", body.GetProperty("params").GetProperty("channel").GetString());
        Assert.EndsWith("/mgmt/load/runs/41/stats", body.GetProperty("statsUrl").GetString());

        var second = await (await CheckInAsync(client, "i-0b")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, second.GetProperty("index").GetInt32());

        var repeat = await CheckInAsync(client, "i-0a");
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal(0, (await repeat.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("index").GetInt32());

        var full = await CheckInAsync(client, "i-0c");
        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        Assert.Equal("run_full", await ErrorAsync(full));

        var run = host.Store.Run(41);
        Assert.Equal(2, run.CheckedIn);
        Assert.Equal(new Dictionary<string, int> { ["i-0a"] = 0, ["i-0b"] = 1 }, LoadJson.ParseCheckins(run.Checkins));
    }

    [Fact]
    public async Task CheckIn_TwoConcurrentInstances_NeverShareAnIndex()
    {
        for (var round = 0; round < 10; round++)
        {
            await using var host = HostWithRun(LoadRunStatus.GeneratorsUp);
            var responses = await Task.WhenAll(
                CheckInAsync(host.Generator(), $"i-0a{round}"),
                CheckInAsync(host.Generator(), $"i-0b{round}"));

            var indexes = new List<int>();
            foreach (var response in responses)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                indexes.Add((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("index").GetInt32());
            }

            Assert.Equal(new[] { 0, 1 }, indexes.OrderBy(i => i));
            Assert.Equal(2, host.Store.Run(41).CheckedIn);
        }
    }

    [Theory]
    [InlineData(LoadRunStatus.Queued)]
    [InlineData(LoadRunStatus.FleetUp)]
    [InlineData(LoadRunStatus.Ramping)]
    [InlineData(LoadRunStatus.Draining)]
    [InlineData(LoadRunStatus.Succeeded)]
    [InlineData(LoadRunStatus.Lost)]
    public async Task CheckIn_409_RunNotAccepting_OutsideGeneratorsUp(string status)
    {
        await using var host = HostWithRun(status);

        var response = await CheckInAsync(host.Generator(), "i-0a");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("run_not_accepting", await ErrorAsync(response));
        Assert.Equal(0, host.Store.Run(41).CheckedIn);
    }

    [Fact]
    public async Task CheckIn_ARepeatDuringRampOrHold_GetsTheSameAssignment_ButNotOnceDraining()
    {
        await using var host = HostWithRun(LoadRunStatus.GeneratorsUp);
        await CheckInAsync(host.Generator(), "i-0a");
        await CheckInAsync(host.Generator(), "i-0b");

        host.Store.Run(41).Status = LoadRunStatus.Ramping;
        var repeat = await CheckInAsync(host.Generator(), "i-0b");
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal(1, (await repeat.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("index").GetInt32());

        host.Store.Run(41).Status = LoadRunStatus.Draining;
        Assert.Equal(HttpStatusCode.Conflict, (await CheckInAsync(host.Generator(), "i-0b")).StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"instanceId\":\"\"}")]
    [InlineData("{\"instanceId\":7}")]
    [InlineData("not json")]
    public async Task CheckIn_400_WithoutAnInstanceId(string json)
    {
        await using var host = HostWithRun(LoadRunStatus.GeneratorsUp);

        var response = await host.Generator().PostAsync("/mgmt/load/runs/41/checkin", LoadTestHost.Json(json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("instanceId", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
    }

    // ---------- stats ----------

    [Fact]
    public async Task Stats_StoresTheLineVerbatim_AndRaisesPeaks()
    {
        await using var host = HostWithRun(LoadRunStatus.Holding);
        var first = LoadTestHost.StatsLine(41, 0, At, 480,
            """{"vus":{"type":"gauge","value":480},"event_lag_ms":{"type":"trend","avg":41,"min":12,"p(99)":310}}""");
        var second = LoadTestHost.StatsLine(41, 1, At.AddSeconds(5), 470,
            """{"vus":{"type":"gauge","value":470},"event_lag_ms":{"type":"trend","avg":55,"min":9,"p(99)":290}}""");

        Assert.Equal(HttpStatusCode.OK, (await StatsAsync(host.Generator(), first)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await StatsAsync(host.Generator(), second)).StatusCode);

        Assert.Equal(new[] { first, second }, host.Store.Stats.Select(s => s.Body));
        Assert.All(host.Store.Stats, s => Assert.Equal(LoadStatSource.Generator, s.Source));
        Assert.Equal(new int?[] { 0, 1 }, host.Store.Stats.Select(s => s.Index));
        Assert.Equal(At, host.Store.Stats[0].At);

        var peaks = LoadJson.ParsePeaks(host.Store.Run(41).Peaks);
        Assert.Equal(480, peaks["vus.value"]);
        Assert.Equal(55, peaks["event_lag_ms.avg"]);
        Assert.Equal(12, peaks["event_lag_ms.min"]);
        Assert.Equal(310, peaks["event_lag_ms.p(99)"]);
        Assert.False(peaks.ContainsKey("event_lag_ms.type"));
    }

    [Fact]
    public async Task Stats_PublishesTheFleetAggregate_OfTheNewestLineOfEachGenerator()
    {
        await using var host = HostWithRun(LoadRunStatus.Holding);
        var client = host.Generator();

        await StatsAsync(client, LoadTestHost.StatsLine(41, 0, At, 400,
            """{"ws_errors":{"type":"counter","count":9,"rate":1},"event_lag_ms":{"type":"trend","max":9000,"min":1}}"""));
        await StatsAsync(client, LoadTestHost.StatsLine(41, 1, At, 470,
            """{"vus":{"type":"gauge","value":470},"ws_errors":{"type":"counter","count":2,"rate":0.5},"event_lag_ms":{"type":"trend","avg":55,"min":9,"med":50,"max":290,"p(95)":200,"p(99)":280}}"""));
        // Generator 0's newer line replaces its first one in the aggregate.
        await StatsAsync(client, LoadTestHost.StatsLine(41, 0, At.AddSeconds(5), 480,
            """{"vus":{"type":"gauge","value":480},"ws_errors":{"type":"counter","count":1,"rate":0},"event_lag_ms":{"type":"trend","avg":41,"min":12,"med":38,"max":410,"p(95)":120,"p(99)":310}}"""));

        var events = host.Events.All.Where(p => p.Channel == LoadEndpoints.OpsLoadChannel && p.Event == "stats").ToList();
        Assert.Equal(3, events.Count);
        var last = JsonSerializer.SerializeToElement(events[^1].Data, Web);
        Assert.Equal(41, last.GetProperty("runId").GetInt64());
        Assert.Equal(At.AddSeconds(5), last.GetProperty("at").GetDateTimeOffset());

        var aggregate = last.GetProperty("aggregate");
        Assert.Equal(2, aggregate.GetProperty("generators").GetInt32());
        Assert.Equal(950, aggregate.GetProperty("vus").GetDouble());
        var metrics = aggregate.GetProperty("metrics");
        Assert.Equal(950, metrics.GetProperty("vus").GetProperty("value").GetDouble());
        Assert.Equal(3, metrics.GetProperty("ws_errors").GetProperty("count").GetDouble());
        Assert.Equal(0.5, metrics.GetProperty("ws_errors").GetProperty("rate").GetDouble());
        var lag = metrics.GetProperty("event_lag_ms");
        Assert.Equal("trend", lag.GetProperty("type").GetString());
        Assert.Equal(55, lag.GetProperty("avg").GetDouble());
        Assert.Equal(9, lag.GetProperty("min").GetDouble());
        Assert.Equal(50, lag.GetProperty("med").GetDouble());
        Assert.Equal(410, lag.GetProperty("max").GetDouble());
        Assert.Equal(200, lag.GetProperty("p(95)").GetDouble());
        Assert.Equal(310, lag.GetProperty("p(99)").GetDouble());
    }

    [Fact]
    public async Task Stats_AnswersContinue_ThenStop_OnceAStopIsRequested_OrWhileDraining()
    {
        await using var host = HostWithRun(LoadRunStatus.Ramping);
        var line = LoadTestHost.StatsLine(41, 0, At, 10, "{}");

        async Task<string?> ActionAsync()
        {
            var response = await StatsAsync(host.Generator(), line);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("action").GetString();
        }

        Assert.Equal("continue", await ActionAsync());

        host.Store.Run(41).StopRequestedAt = At;
        Assert.Equal("stop", await ActionAsync());

        host.Store.Run(41).StopRequestedAt = null;
        host.Store.Run(41).Status = LoadRunStatus.Draining;
        Assert.Equal("stop", await ActionAsync());

        // LoadGeneratorStats: one per stored line, by scenario.
        Assert.Equal(new[] { "tracker", "tracker", "tracker" }, host.Metrics.StatsLines);
    }

    [Theory]
    [InlineData(LoadRunStatus.Succeeded)]
    [InlineData(LoadRunStatus.Stopped)]
    [InlineData(LoadRunStatus.Expired)]
    [InlineData(LoadRunStatus.Failed)]
    [InlineData(LoadRunStatus.Lost)]
    public async Task Stats_409_WhenTheRunIsTerminal_AndNothingIsStored(string status)
    {
        await using var host = HostWithRun(status);

        var response = await StatsAsync(host.Generator(), LoadTestHost.StatsLine(41, 0, At, 10, "{}"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("run_terminal", await ErrorAsync(response));
        Assert.Empty(host.Store.Stats);
    }

    public static TheoryData<string, string> InvalidLines => new()
    {
        { "not json", "body" },
        { "[1,2]", "body" },
        { """{"runId":40,"index":0,"at":"2026-11-05T02:14:05Z","vus":1}""", "runId" },
        { """{"index":0,"at":"2026-11-05T02:14:05Z","vus":1}""", "runId" },
        { """{"runId":41,"index":2,"at":"2026-11-05T02:14:05Z","vus":1}""", "index" },
        { """{"runId":41,"index":-1,"at":"2026-11-05T02:14:05Z","vus":1}""", "index" },
        { """{"runId":41,"index":0.5,"at":"2026-11-05T02:14:05Z","vus":1}""", "index" },
        { """{"runId":41,"index":0,"vus":1}""", "at" },
        { """{"runId":41,"index":0,"at":"yesterday","vus":1}""", "at" },
        { """{"runId":41,"index":0,"at":"2026-11-05T02:14:05Z"}""", "vus" },
        { """{"runId":41,"index":0,"at":"2026-11-05T02:14:05Z","vus":-1}""", "vus" },
        { """{"runId":41,"index":0,"at":"2026-11-05T02:14:05Z","vus":1,"metrics":[]}""", "metrics" },
    };

    [Theory]
    [MemberData(nameof(InvalidLines))]
    public async Task Stats_400_NamesTheMissingOrBadField(string line, string field)
    {
        await using var host = HostWithRun(LoadRunStatus.Holding);

        var response = await StatsAsync(host.Generator(), line);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(field, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
        Assert.Empty(host.Store.Stats);
    }
}
