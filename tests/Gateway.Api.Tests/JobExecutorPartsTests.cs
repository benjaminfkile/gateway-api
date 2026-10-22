using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Gateway.Api.Containers;
using Gateway.Api.Data;
using Gateway.Api.Jobs;
using Gateway.Api.Management;
using Gateway.Api.Reconcile;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Api.Tests;

/// <summary>
/// The pieces the job executor is built from (docs/jobs.md section 3): the job log
/// config, the ref-based env read, the run env, and the run metrics.
/// </summary>
public class JobExecutorPartsTests
{
    // ---------- LogConfigFactory.ForJob ----------

    [Fact]
    public void ForJob_WritesTheJobGroup_AndOneStreamPerRun()
    {
        var configured = new LogDriverConfig { Driver = "awslogs", Options = { ["awslogs-endpoint"] = "custom" } };

        var config = LogConfigFactory.ForJob("mirror", 42, "xx-test-1", configured);

        Assert.Equal("awslogs", config.Driver);
        Assert.Equal("/gateway/jobs/mirror", config.Options["awslogs-group"]);
        Assert.Equal("run-42", config.Options["awslogs-stream"]);
        Assert.Equal("true", config.Options["awslogs-create-group"]);
        Assert.Equal("custom", config.Options["awslogs-endpoint"]);
        Assert.True(config.Options.ContainsKey("awslogs-region"));
        Assert.True(LogConfigFactory.UsesAwsLogs(configured));
    }

    [Fact]
    public void ForJob_HonoursTheJsonFileEscapeHatch_ExactlyAsForServices()
    {
        var configured = new LogDriverConfig { Driver = "json-file" };

        var job = LogConfigFactory.ForJob("mirror", 42, null, configured);
        var service = LogConfigFactory.ForService("svc-a", "i-0000000000000000a", null, configured);

        Assert.Equal("json-file", job.Driver);
        Assert.Equal(service.Driver, job.Driver);
        Assert.Equal(service.Options, job.Options);
        Assert.False(LogConfigFactory.UsesAwsLogs(configured));
    }

    // ---------- SecretsManagerEnvProvider by ref ----------

    [Fact]
    public async Task GetEnvByRef_EmptyForNoRef_WithoutTouchingTheStore()
    {
        var store = new FakeSecretStore();
        var provider = new SecretsManagerEnvProvider(store, TimeSpan.FromMinutes(1));

        Assert.Empty(await provider.GetEnvAsync((string?)null));
        Assert.Empty(await provider.GetEnvAsync("  "));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task GetEnvByRef_SharesTheCacheWithTheManifestOverload()
    {
        var store = new FakeSecretStore();
        store.Secrets["gateway/jobs/mirror"] = "{\"KEY\":\"value\"}";
        var provider = new SecretsManagerEnvProvider(store, TimeSpan.FromMinutes(1));

        var byRef = await provider.GetEnvAsync("gateway/jobs/mirror");
        var byManifest = await provider.GetEnvAsync(new ServiceManifest { Name = "svc-a", EnvSecretRef = "gateway/jobs/mirror" });

        Assert.Equal("value", byRef["KEY"]);
        Assert.Equal(byRef, byManifest);
        Assert.Equal(1, store.Calls);
    }

    // ---------- JobEnv ----------

    [Fact]
    public void JobEnv_AddsTheFourVariables_AndTheyWinOverTheSecret()
    {
        var job = JobTestHost.Job("mirror", script: "echo é");
        var run = new JobRun { Id = 9, Job = "mirror", Trigger = JobRunTrigger.Schedule };
        var secret = new Dictionary<string, string> { ["A"] = "1", [JobEnv.Script] = "spoofed" };

        var env = JobEnv.Build(secret, job, run);

        Assert.Equal("1", env["A"]);
        Assert.Equal("mirror", env["GATEWAY_JOB_NAME"]);
        Assert.Equal("9", env["GATEWAY_JOB_RUN_ID"]);
        Assert.Equal("schedule", env["GATEWAY_JOB_TRIGGER"]);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("echo é")), env["GATEWAY_JOB_SCRIPT"]);
    }

    // ---------- metrics ----------

    [Fact]
    public void Emf_CarriesCountByJobAndStatus_AndDurationByJob()
    {
        var at = new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

        using var doc = JsonDocument.Parse(JobMetrics.BuildEmf("Gateway", "mirror", "failed", TimeSpan.FromSeconds(12.5), at));
        var root = doc.RootElement;

        Assert.Equal("mirror", root.GetProperty("Job").GetString());
        Assert.Equal("failed", root.GetProperty("Status").GetString());
        Assert.Equal(1, root.GetProperty("JobRunCount").GetInt32());
        Assert.Equal(12.5, root.GetProperty("JobRunDuration").GetDouble());

        var aws = root.GetProperty("_aws");
        Assert.Equal(at.ToUnixTimeMilliseconds(), aws.GetProperty("Timestamp").GetInt64());
        var directives = aws.GetProperty("CloudWatchMetrics").EnumerateArray().ToList();
        Assert.Equal(2, directives.Count);
        Assert.All(directives, d => Assert.Equal("Gateway", d.GetProperty("Namespace").GetString()));
        Assert.Equal(new[] { "Job", "Status" },
            directives[0].GetProperty("Dimensions")[0].EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("JobRunCount", directives[0].GetProperty("Metrics")[0].GetProperty("Name").GetString());
        Assert.Equal(new[] { "Job" },
            directives[1].GetProperty("Dimensions")[0].EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("JobRunDuration", directives[1].GetProperty("Metrics")[0].GetProperty("Name").GetString());
    }

    [Fact]
    public void Emf_WithoutDuration_CarriesOnlyTheCount()
    {
        using var doc = JsonDocument.Parse(JobMetrics.BuildEmf("Gateway", "mirror", "lost", null, DateTimeOffset.UnixEpoch));

        Assert.False(doc.RootElement.TryGetProperty("JobRunDuration", out _));
        Assert.Single(doc.RootElement.GetProperty("_aws").GetProperty("CloudWatchMetrics").EnumerateArray());
    }

    [Fact]
    public async Task JobMetrics_RecordsOnTheMeter_AndSendsEmfToTheAgentEndpoint()
    {
        using var agent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)agent.Client.LocalEndPoint!).Port;
        using var metrics = new JobMetrics(
            new JobSchedulerOptions { MetricsEndpoint = $"127.0.0.1:{port}" },
            NullLogger<JobMetrics>.Instance);

        var counts = new List<(long Value, Dictionary<string, object?> Tags)>();
        var durations = new List<(double Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == JobMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            counts.Add((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            durations.Add((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        listener.Start();

        metrics.RunFinished("mirror", "succeeded", TimeSpan.FromSeconds(3));

        var count = Assert.Single(counts);
        Assert.Equal(1, count.Value);
        Assert.Equal("mirror", count.Tags["job"]);
        Assert.Equal("succeeded", count.Tags["status"]);
        var duration = Assert.Single(durations);
        Assert.Equal(3, duration.Value);
        Assert.Equal("mirror", duration.Tags["job"]);

        var received = await agent.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var doc = JsonDocument.Parse(received.Buffer);
        Assert.Equal("succeeded", doc.RootElement.GetProperty("Status").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("JobRunDuration").GetDouble());
    }

    [Fact]
    public void JobMetrics_WithNoEndpoint_OrABadOne_NeverThrows()
    {
        using var off = new JobMetrics(new JobSchedulerOptions { MetricsEndpoint = "" }, NullLogger<JobMetrics>.Instance);
        using var bad = new JobMetrics(new JobSchedulerOptions { MetricsEndpoint = "not-an-endpoint" }, NullLogger<JobMetrics>.Instance);

        off.RunFinished("mirror", "failed", null);
        bad.RunFinished("mirror", "failed", TimeSpan.FromSeconds(1));
    }
}

/// <summary>
/// The registration <c>Program</c> uses when a database is configured: the scheduler
/// resolves from the reconciler's seams (runtime, env provider by ref, leader status).
/// </summary>
public class JobSchedulerRegistrationTests
{
    [Fact]
    public void AddJobScheduler_ResolvesTheHostedServiceFromTheReconcilerSeams()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jobs:TickInterval"] = "00:00:02",
                ["Jobs:MaxConcurrent"] = "2",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddManagementServices();
        services.AddNodeReconciler(configuration);
        var before = services.Count;
        services.AddJobScheduler(configuration);
        var descriptor = Assert.Single(services.Skip(before), d => d.ServiceType == typeof(IHostedService));
        using var provider = services.BuildServiceProvider();

        // Only the scheduler's own registration is resolved: the reconciler's hosted
        // service needs the proxy stack, which this test does not build.
        Assert.IsType<JobSchedulerService>(descriptor.ImplementationFactory!(provider));

        var options = provider.GetRequiredService<JobSchedulerOptions>();
        Assert.Equal(TimeSpan.FromSeconds(2), options.TickInterval);
        Assert.Equal(2, options.MaxConcurrent);
        Assert.Equal(1L << 30, options.MemoryLimitBytes);

        // One provider instance behind both env seams, and one leadership snapshot
        // behind both leader seams.
        Assert.Same(provider.GetRequiredService<IServiceEnvProvider>(), provider.GetRequiredService<ISecretEnvProvider>());
        Assert.Same(
            provider.GetRequiredService<Gateway.Api.Instances.LeadershipState>(),
            provider.GetRequiredService<Gateway.Api.Instances.ILeaderStatus>());
    }
}
