using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Gateway.Api.Management;
using Gateway.Api.RealTime;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Api.Tests;

/// <summary>
/// Integration coverage for <c>/mgmt/jobs</c> (docs/jobs.md section 4): the
/// upsert rules (validation, tri-state, script versions, next_run_at), run enqueue
/// and its 409, cancel, delete with and without force, the list shape, the log
/// read, and the <c>ops:jobs</c> events.
/// </summary>
public class JobEndpointsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static JsonElement Data(JobTestHost.RecordingPublisher.Published p) =>
        JsonSerializer.SerializeToElement(p.Data, Web);

    private static object CreateBody(string script = "echo hi") => new
    {
        schedule = "0 4 * * 1",
        timeZone = "America/Chicago",
        script,
    };

    // ---------- upsert ----------

    [Fact]
    public async Task Put_Creates_201_WithDefaults_AndVersionOne()
    {
        await using var host = new JobTestHost();
        var response = await host.Admin().PutAsJsonAsync("/mgmt/jobs/nightly", CreateBody());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("nightly", body.GetProperty("name").GetString());
        Assert.Equal("0 4 * * 1", body.GetProperty("schedule").GetString());
        Assert.Equal("America/Chicago", body.GetProperty("timeZone").GetString());
        Assert.Equal(JobEndpoints.FallbackImage, body.GetProperty("image").GetString());
        Assert.Equal(JobEndpoints.FallbackTag, body.GetProperty("tag").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("envSecretRef").ValueKind);
        Assert.Equal(3600, body.GetProperty("timeoutSeconds").GetInt32());
        Assert.True(body.GetProperty("enabled").GetBoolean());
        Assert.Equal(1, body.GetProperty("scriptVersion").GetInt32());
        Assert.Equal(JsonValueKind.String, body.GetProperty("nextRunAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lastRun").ValueKind);
        Assert.Equal("alice-admin", body.GetProperty("updatedBy").GetString());
        Assert.Equal("echo hi", body.GetProperty("script").GetString());

        await host.Factory.WithDbAsync(async db =>
        {
            var version = await db.JobScriptVersions.SingleAsync(v => v.Job == "nightly");
            Assert.Equal(1, version.Version);
            Assert.Equal(JobEndpoints.Sha256Hex("echo hi"), version.Sha256);
            Assert.Equal(64, version.Sha256.Length);
        });
    }

    [Fact]
    public async Task Put_WithoutImageOrTag_UsesConfiguredDefaults()
    {
        await using var host = new JobTestHost();
        var app = host.Factory.WithWebHostBuilder(b => b.UseSetting(JobEndpoints.DefaultImageKey, "registry.example.com/runner")
            .UseSetting(JobEndpoints.DefaultTagKey, "v7"));
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", ManagementApiFactory.AdminToken());

        var body = await (await client.PutAsJsonAsync("/mgmt/jobs/nightly", CreateBody()))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("registry.example.com/runner", body.GetProperty("image").GetString());
        Assert.Equal("v7", body.GetProperty("tag").GetString());
    }

    [Fact]
    public async Task Put_Update_AbsentPreserves_EmptyClears()
    {
        await using var host = new JobTestHost();
        var client = host.Admin();
        await client.PutAsJsonAsync("/mgmt/jobs/nightly", new
        {
            schedule = "0 4 * * 1",
            timeZone = "America/Chicago",
            script = "echo hi",
            image = "registry.example.com/runner",
            tag = "v1",
            envSecretRef = "gateway/jobs/nightly",
            timeoutSeconds = 600,
            enabled = false,
        });

        // An empty body preserves everything.
        var preserved = await client.PutAsJsonAsync("/mgmt/jobs/nightly", new { });
        Assert.Equal(HttpStatusCode.OK, preserved.StatusCode);
        var p = await preserved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("0 4 * * 1", p.GetProperty("schedule").GetString());
        Assert.Equal("America/Chicago", p.GetProperty("timeZone").GetString());
        Assert.Equal("registry.example.com/runner", p.GetProperty("image").GetString());
        Assert.Equal("v1", p.GetProperty("tag").GetString());
        Assert.Equal("gateway/jobs/nightly", p.GetProperty("envSecretRef").GetString());
        Assert.Equal(600, p.GetProperty("timeoutSeconds").GetInt32());
        Assert.False(p.GetProperty("enabled").GetBoolean());
        Assert.Equal("echo hi", p.GetProperty("script").GetString());
        Assert.Equal(1, p.GetProperty("scriptVersion").GetInt32());

        // Empty strings clear the nullable schedule and secret ref.
        var cleared = await client.PutAsJsonAsync("/mgmt/jobs/nightly", new { schedule = "", envSecretRef = "", enabled = true });
        var c = await cleared.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, c.GetProperty("schedule").ValueKind);
        Assert.Equal(JsonValueKind.Null, c.GetProperty("envSecretRef").ValueKind);
        Assert.True(c.GetProperty("enabled").GetBoolean());
        // Manual only: no next run even though enabled.
        Assert.Equal(JsonValueKind.Null, c.GetProperty("nextRunAt").ValueKind);

        await host.Factory.WithDbAsync(async db =>
        {
            var job = await db.Jobs.SingleAsync(j => j.Name == "nightly");
            Assert.Null(job.Schedule);
            Assert.Null(job.EnvSecretRef);
            Assert.Equal("registry.example.com/runner", job.Image);
        });
    }

    [Fact]
    public async Task Put_ScriptVersion_BumpsOnlyWhenTheBodyChanges()
    {
        await using var host = new JobTestHost();
        var client = host.Admin();
        await client.PutAsJsonAsync("/mgmt/jobs/nightly", CreateBody("echo one"));

        var same = await (await client.PutAsJsonAsync("/mgmt/jobs/nightly", new { script = "echo one", timeoutSeconds = 120 }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, same.GetProperty("scriptVersion").GetInt32());

        var changed = await (await client.PutAsJsonAsync("/mgmt/jobs/nightly", new { script = "echo two" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, changed.GetProperty("scriptVersion").GetInt32());

        var versions = await client.GetFromJsonAsync<JsonElement>("/mgmt/jobs/nightly/script/versions");
        Assert.Equal(2, versions.GetArrayLength());
        Assert.Equal(2, versions[0].GetProperty("version").GetInt32());
        Assert.Equal(JobEndpoints.Sha256Hex("echo two"), versions[0].GetProperty("sha256").GetString());
        Assert.Equal("alice-admin", versions[0].GetProperty("updatedBy").GetString());
        Assert.True(versions[0].TryGetProperty("updatedAt", out _));
        Assert.False(versions[0].TryGetProperty("script", out _));
        Assert.Equal(1, versions[1].GetProperty("version").GetInt32());

        var v1 = await client.GetFromJsonAsync<JsonElement>("/mgmt/jobs/nightly/script/versions/1");
        Assert.Equal(1, v1.GetProperty("version").GetInt32());
        Assert.Equal("echo one", v1.GetProperty("script").GetString());

        var missing = await client.GetAsync("/mgmt/jobs/nightly/script/versions/9");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    public static TheoryData<string, string, string> InvalidCreates => new()
    {
        // name, body json, field
        { "Bad_Name", """{"timeZone":"UTC","script":"x"}""", "name" },
        { "-leading-dash", """{"timeZone":"UTC","script":"x"}""", "name" },
        { "a", """{"timeZone":"UTC","script":"x"}""", "name" },
        { new string('a', 64), """{"timeZone":"UTC","script":"x"}""", "name" },
        { "nightly", """{"script":"x"}""", "timeZone" },
        { "nightly", """{"timeZone":"UTC"}""", "script" },
        { "nightly", """{"timeZone":"UTC","script":"x","schedule":"0 0 4 * * *"}""", "schedule" },
        { "nightly", """{"timeZone":"UTC","script":"x","schedule":"every day"}""", "schedule" },
        { "nightly", """{"timeZone":"Mars/Base","script":"x"}""", "timeZone" },
        { "nightly", """{"timeZone":"","script":"x"}""", "timeZone" },
        { "nightly", """{"timeZone":"UTC","script":"x","timeoutSeconds":29}""", "timeoutSeconds" },
        { "nightly", """{"timeZone":"UTC","script":"x","timeoutSeconds":21601}""", "timeoutSeconds" },
        { "nightly", """{"timeZone":"UTC","script":""}""", "script" },
        { "nightly", """{"timeZone":"UTC","script":"x","image":" "}""", "image" },
        { "nightly", """{"timeZone":"UTC","script":"x","tag":""}""", "tag" },
    };

    [Theory]
    [MemberData(nameof(InvalidCreates))]
    public async Task Put_Validation_400_NamesTheField(string name, string json, string field)
    {
        await using var host = new JobTestHost();
        var response = await host.Admin().PutAsync(
            $"/mgmt/jobs/{name}", new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(field, body.GetProperty("field").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("error").GetString()));
        await host.Factory.WithDbAsync(async db => Assert.Empty(await db.Jobs.ToListAsync()));
    }

    [Fact]
    public async Task Put_ScriptSizeLimit_IsInUtf8Bytes()
    {
        await using var host = new JobTestHost();
        var client = host.Admin();

        var atLimit = new string('a', JobEndpoints.MaxScriptBytes);
        var ok = await client.PutAsJsonAsync("/mgmt/jobs/nightly", new { timeZone = "UTC", script = atLimit });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        // Half as many characters, but each is two UTF-8 bytes, plus one more.
        var overLimit = new string('é', JobEndpoints.MaxScriptBytes / 2) + "a";
        var tooBig = await client.PutAsJsonAsync("/mgmt/jobs/nightly", new { script = overLimit });
        Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);
        var body = await tooBig.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("script", body.GetProperty("field").GetString());
    }

    [Fact]
    public async Task Put_Update_ValidatesChangedFields()
    {
        await using var host = new JobTestHost();
        await host.SeedAsync(JobTestHost.Job("nightly"), JobTestHost.Version("nightly", 1, "echo hi"));
        var client = host.Admin();

        var badSchedule = await client.PutAsJsonAsync("/mgmt/jobs/nightly", new { schedule = "0 0 4 * * *" });
        Assert.Equal(HttpStatusCode.BadRequest, badSchedule.StatusCode);

        var emptyScript = await client.PutAsJsonAsync("/mgmt/jobs/nightly", new { script = "" });
        Assert.Equal(HttpStatusCode.BadRequest, emptyScript.StatusCode);
        Assert.Equal("script", (await emptyScript.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());

        // Update needs neither timeZone nor script.
        var ok = await client.PutAsJsonAsync("/mgmt/jobs/nightly", new { timeoutSeconds = 30 });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task NextRunAt_UsesTheJobZone_AcrossSpringForward_AndFollowsEnableDisable()
    {
        // Saturday 2026-03-07 12:00Z; Chicago moves to CDT at 02:00 on Sunday 03-08.
        var clock = new ManualTimeProvider { Now = new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.Zero) };
        await using var host = new JobTestHost(clock);
        var client = host.Admin();

        var created = await (await client.PutAsJsonAsync("/mgmt/jobs/nightly", new
        {
            schedule = "0 4 * * *",
            timeZone = "America/Chicago",
            script = "echo hi",
        })).Content.ReadFromJsonAsync<JsonElement>();

        // 04:00 CDT on 03-08 is 09:00Z (it would be 10:00Z under CST).
        var expected = new DateTimeOffset(2026, 3, 8, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, created.GetProperty("nextRunAt").GetDateTimeOffset());

        var disabled = await (await client.PostAsync("/mgmt/jobs/nightly/disable", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(disabled.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, disabled.GetProperty("nextRunAt").ValueKind);
        Assert.False(disabled.TryGetProperty("script", out _));

        clock.Now = new DateTimeOffset(2026, 3, 8, 9, 30, 0, TimeSpan.Zero);
        var enabled = await (await client.PostAsync("/mgmt/jobs/nightly/enable", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(enabled.GetProperty("enabled").GetBoolean());
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 9, 0, 0, TimeSpan.Zero), enabled.GetProperty("nextRunAt").GetDateTimeOffset());

        await host.Factory.WithDbAsync(async db =>
        {
            var job = await db.Jobs.SingleAsync(j => j.Name == "nightly");
            Assert.Equal(new DateTimeOffset(2026, 3, 9, 9, 0, 0, TimeSpan.Zero), job.NextRunAt);
        });
    }

    // ---------- reads ----------

    [Fact]
    public async Task List_NeverCarriesTheScript_AndIncludesLastRun()
    {
        await using var host = new JobTestHost();
        await host.SeedAsync(
            JobTestHost.Job("alpha", script: "echo secret-ish"),
            JobTestHost.Job("beta", schedule: null),
            JobTestHost.Run(1, "alpha", JobRunStatus.Succeeded),
            JobTestHost.Run(2, "alpha", JobRunStatus.Failed));

        var list = await host.Client(ManagementApiFactory.DeployToken()).GetFromJsonAsync<JsonElement>("/mgmt/jobs");

        Assert.Equal(2, list.GetArrayLength());
        foreach (var job in list.EnumerateArray())
        {
            Assert.False(job.TryGetProperty("script", out _));
            foreach (var key in new[] { "name", "schedule", "timeZone", "image", "tag", "envSecretRef", "timeoutSeconds", "enabled", "scriptVersion", "nextRunAt", "lastRun", "updatedBy", "updatedAt" })
            {
                Assert.True(job.TryGetProperty(key, out _), key);
            }
        }

        var alpha = list[0];
        Assert.Equal("alpha", alpha.GetProperty("name").GetString());
        Assert.Equal(2, alpha.GetProperty("lastRun").GetProperty("id").GetInt64());
        Assert.Equal("failed", alpha.GetProperty("lastRun").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, list[1].GetProperty("lastRun").ValueKind);
        Assert.Equal(JsonValueKind.Null, list[1].GetProperty("schedule").ValueKind);
    }

    [Fact]
    public async Task Get_Unknown_404()
    {
        await using var host = new JobTestHost();
        var client = host.Admin();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/mgmt/jobs/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/mgmt/jobs/nope/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/mgmt/jobs/nope/enable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/mgmt/jobs/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/mgmt/jobs/nope/runs/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/mgmt/jobs/nope/script/versions")).StatusCode);
    }

    [Fact]
    public async Task Runs_NewestFirst_WithLimitAndBefore_AndRunOfAnotherJob404()
    {
        await using var host = new JobTestHost();
        await host.SeedAsync(
            JobTestHost.Job("alpha"),
            JobTestHost.Job("beta"),
            JobTestHost.Run(1, "alpha", JobRunStatus.Succeeded),
            JobTestHost.Run(2, "beta", JobRunStatus.Succeeded),
            JobTestHost.Run(3, "alpha", JobRunStatus.Failed),
            JobTestHost.Run(4, "alpha", JobRunStatus.TimedOut));
        var client = host.Client(ManagementApiFactory.DeployToken());

        var all = await client.GetFromJsonAsync<JsonElement>("/mgmt/jobs/alpha/runs");
        Assert.Equal(new long[] { 4, 3, 1 }, all.EnumerateArray().Select(r => r.GetProperty("id").GetInt64()));

        var page = await client.GetFromJsonAsync<JsonElement>("/mgmt/jobs/alpha/runs?limit=1&before=4");
        Assert.Equal(3, Assert.Single(page.EnumerateArray()).GetProperty("id").GetInt64());

        var run = await client.GetFromJsonAsync<JsonElement>("/mgmt/jobs/alpha/runs/3");
        foreach (var key in new[] { "id", "job", "trigger", "actor", "status", "instanceId", "imageDigest", "scriptVersion", "exitCode", "queuedAt", "startedAt", "finishedAt", "cancelRequestedAt", "detail" })
        {
            Assert.True(run.TryGetProperty(key, out _), key);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/mgmt/jobs/alpha/runs/2")).StatusCode);
    }

    [Fact]
    public async Task Log_ReadsTheJobGroupAndRunStream()
    {
        await using var host = new JobTestHost();
        await host.SeedAsync(JobTestHost.Job("nightly"), JobTestHost.Run(7, "nightly", JobRunStatus.Succeeded));
        host.Factory.LogStore.SeedStream(
            "/gateway/jobs/nightly",
            "run-7",
            new LogLine(DateTimeOffset.UnixEpoch, "start"),
            new LogLine(DateTimeOffset.UnixEpoch.AddSeconds(1), "done"));

        var lines = await host.Admin().GetFromJsonAsync<JsonElement>("/mgmt/jobs/nightly/runs/7/log?tail=1");

        Assert.Equal("done", Assert.Single(lines.EnumerateArray()).GetProperty("message").GetString());
        Assert.Equal(("/gateway/jobs/nightly", "run-7", 1), Assert.Single(host.Factory.LogStore.StreamQueries));
        Assert.Equal("/gateway/jobs/nightly", CloudWatchLogStore.JobLogGroupFor("nightly"));
        // The service log path is untouched.
        Assert.Empty(host.Factory.LogStore.Queries);
    }

    // ---------- run / cancel / delete ----------

    [Fact]
    public async Task Run_EnqueuesQueuedManualRun_ThenConflicts_EvenWhenDisabled()
    {
        await using var host = new JobTestHost();
        await host.SeedAsync(JobTestHost.Job("nightly", enabled: false));
        var client = host.Client(ManagementApiFactory.JobsToken());

        var response = await client.PostAsync("/mgmt/jobs/nightly/run", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var runId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("runId").GetInt64();

        var again = await client.PostAsync("/mgmt/jobs/nightly/run", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("run_active", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        await host.Factory.WithDbAsync(async db =>
        {
            var run = await db.JobRuns.SingleAsync();
            Assert.Equal(runId, run.Id);
            Assert.Equal(JobRunStatus.Queued, run.Status);
            Assert.Equal(JobRunTrigger.Manual, run.Trigger);
            Assert.Equal("jobs-client", run.Actor);
            Assert.Equal(1, run.ScriptVersion);
            Assert.Null(run.InstanceId);
        });

        var evt = Data(Assert.Single(host.Events.OnJobs("run")));
        Assert.Equal(runId, evt.GetProperty("runId").GetInt64());
        Assert.Equal("nightly", evt.GetProperty("job").GetString());
        Assert.Equal("queued", evt.GetProperty("status").GetString());
        Assert.Equal("manual", evt.GetProperty("trigger").GetString());
        Assert.Equal("jobs-client", evt.GetProperty("actor").GetString());
        foreach (var key in new[] { "instanceId", "startedAt", "finishedAt", "exitCode" })
        {
            Assert.Equal(JsonValueKind.Null, evt.GetProperty(key).ValueKind);
        }
    }

    [Fact]
    public async Task Cancel_QueuedRun_BecomesCancelled_FinishedRun409()
    {
        await using var host = new JobTestHost();
        await host.SeedAsync(
            JobTestHost.Job("nightly"),
            JobTestHost.Run(1, "nightly", JobRunStatus.Succeeded),
            JobTestHost.Run(2, "nightly", JobRunStatus.Queued));
        var client = host.Admin();

        var response = await client.PostAsync("/mgmt/jobs/nightly/runs/2/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var run = await client.GetFromJsonAsync<JsonElement>("/mgmt/jobs/nightly/runs/2");
        Assert.Equal("cancelled", run.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, run.GetProperty("finishedAt").ValueKind);
        Assert.Equal(JsonValueKind.String, run.GetProperty("cancelRequestedAt").ValueKind);

        var evt = Data(Assert.Single(host.Events.OnJobs("run")));
        Assert.Equal("cancelled", evt.GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/mgmt/jobs/nightly/runs/2/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/mgmt/jobs/nightly/runs/1/cancel", null)).StatusCode);

        // The slot is free again.
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/mgmt/jobs/nightly/run", null)).StatusCode);
    }

    [Fact]
    public async Task Cancel_RunningRun_StampsCancelRequestedOnly()
    {
        await using var host = new JobTestHost();
        await host.SeedAsync(
            JobTestHost.Job("nightly"),
            JobTestHost.Run(1, "nightly", JobRunStatus.Running, instanceId: "i-0123456789abcdef0"));

        var response = await host.Admin().PostAsync("/mgmt/jobs/nightly/runs/1/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await host.Factory.WithDbAsync(async db =>
        {
            var run = await db.JobRuns.SingleAsync();
            Assert.Equal(JobRunStatus.Running, run.Status);
            Assert.NotNull(run.CancelRequestedAt);
            Assert.Null(run.FinishedAt);
        });
        Assert.Empty(host.Events.OnJobs("run"));
    }

    [Fact]
    public async Task Delete_WithActiveRun_409_UnlessForced_HistoryStays()
    {
        await using var host = new JobTestHost();
        await host.SeedAsync(
            JobTestHost.Job("nightly"),
            JobTestHost.Version("nightly", 1, "echo hi"),
            JobTestHost.Run(1, "nightly", JobRunStatus.Succeeded),
            JobTestHost.Run(2, "nightly", JobRunStatus.Queued));
        var client = host.Client(ManagementApiFactory.JobsToken());

        var refused = await client.DeleteAsync("/mgmt/jobs/nightly");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("run_active", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        var forced = await client.DeleteAsync("/mgmt/jobs/nightly?force=true");
        Assert.Equal(HttpStatusCode.NoContent, forced.StatusCode);

        await host.Factory.WithDbAsync(async db =>
        {
            Assert.Empty(await db.Jobs.ToListAsync());
            Assert.Empty(await db.JobScriptVersions.ToListAsync());
            var runs = await db.JobRuns.OrderBy(r => r.Id).ToListAsync();
            Assert.Equal(2, runs.Count);
            Assert.Equal(JobRunStatus.Cancelled, runs[1].Status);
        });

        Assert.Equal("cancelled", Data(Assert.Single(host.Events.OnJobs("run"))).GetProperty("status").GetString());
        var deleted = Data(Assert.Single(host.Events.OnJobs("job")));
        Assert.Equal("deleted", deleted.GetProperty("action").GetString());
        Assert.Equal("jobs-client", deleted.GetProperty("by").GetString());

        // History is still readable after the job is gone.
        var history = await host.Admin().GetFromJsonAsync<JsonElement>("/mgmt/jobs/nightly/runs");
        Assert.Equal(2, history.GetArrayLength());
    }

    [Fact]
    public async Task Delete_WithoutActiveRun_204()
    {
        await using var host = new JobTestHost();
        await host.SeedAsync(JobTestHost.Job("nightly"), JobTestHost.Run(1, "nightly", JobRunStatus.Failed));

        var response = await host.Admin().DeleteAsync("/mgmt/jobs/nightly");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(host.Events.OnJobs("run"));
        await host.Factory.WithDbAsync(async db => Assert.Single(await db.JobRuns.ToListAsync()));
    }

    // ---------- events ----------

    [Fact]
    public async Task JobEvents_PublishedOnUpsertEnableDisable()
    {
        await using var host = new JobTestHost();
        var client = host.Admin();

        await client.PutAsJsonAsync("/mgmt/jobs/nightly", CreateBody());
        await client.PostAsync("/mgmt/jobs/nightly/disable", null);
        await client.PostAsync("/mgmt/jobs/nightly/enable", null);

        var events = host.Events.OnJobs("job").Select(Data).ToList();
        Assert.Equal(new[] { "upserted", "disabled", "enabled" }, events.Select(e => e.GetProperty("action").GetString()));
        Assert.All(events, e =>
        {
            Assert.Equal("nightly", e.GetProperty("name").GetString());
            Assert.Equal("alice-admin", e.GetProperty("by").GetString());
        });
    }

    [Fact]
    public async Task Upsert_Succeeds_EvenWhenThePublishThrows()
    {
        await using var factory = new ManagementApiFactory();
        var throwingHub = new FakeGatewayHubContext { SendError = new InvalidOperationException("backplane down") };
        var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IChannelEventPublisher>();
            services.AddSingleton<IChannelEventPublisher>(
                new ChannelEventPublisher(throwingHub, NullLogger<ChannelEventPublisher>.Instance));
        }));
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", ManagementApiFactory.AdminToken());

        Assert.Equal(HttpStatusCode.Created, (await client.PutAsJsonAsync("/mgmt/jobs/nightly", CreateBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/mgmt/jobs/nightly/run", null)).StatusCode);
    }
}
