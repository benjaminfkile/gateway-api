using System.Net.Http.Headers;
using System.Text;
using Gateway.Api.Data;
using Gateway.Api.Load;
using Gateway.Api.Management;
using Gateway.Api.RealTime;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gateway.Api.Tests;

/// <summary>
/// A <see cref="ManagementApiFactory"/> for the load endpoints: a
/// <see cref="FakeLoadStore"/> and <see cref="FakeGeneratorGroupScaler"/> in place of
/// the real seams, a
/// <see cref="FakeLoadMetrics"/>, a recording publisher
/// (so <c>ops:load</c> events can be asserted), fixed <see cref="LoadOptions"/>, a
/// generous run-token rate limiter unless a test passes its own, and an optional fixed
/// load clock. The manifest and instance status stay on the factory's Sqlite database.
/// </summary>
public sealed class LoadTestHost : IAsyncDisposable
{
    /// <summary>The generator group the options name and the fake scaler knows.</summary>
    public const string GeneratorGroup = "load-generators-placeholder";

    /// <summary>The run token every seeded run accepts.</summary>
    public const string Token = "run-token-placeholder";

    /// <summary>The target service <see cref="SeedTargetAsync"/> creates.</summary>
    public const string Target = "svc-a";

    private readonly WebApplicationFactory<Program> _app;

    public LoadTestHost(
        TimeProvider? clock = null,
        Action<LoadOptions>? configure = null,
        LoadTokenRateLimiter? limiter = null)
    {
        Options = new LoadOptions { GeneratorGroup = GeneratorGroup };
        configure?.Invoke(Options);
        Scaler.Groups[GeneratorGroup] = new GeneratorGroupSize(Desired: 0, Max: 2, InService: 0);

        _app = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IChannelEventPublisher>();
            services.AddSingleton<IChannelEventPublisher>(Events);

            services.RemoveAll<ILoadStore>();
            services.AddSingleton<ILoadStore>(Store);

            services.RemoveAll<IGeneratorGroupScaler>();
            services.AddSingleton<IGeneratorGroupScaler>(Scaler);

            services.RemoveAll<ILoadMetrics>();
            services.AddSingleton<ILoadMetrics>(Metrics);

            services.RemoveAll<LoadOptions>();
            services.AddSingleton(Options);

            services.RemoveAll<LoadTokenRateLimiter>();
            services.AddSingleton(limiter ?? new LoadTokenRateLimiter(rate: 10_000, burst: 10_000));

            if (clock is not null)
            {
                services.AddKeyedSingleton(LoadEndpoints.ClockKey, clock);
            }
        }));
    }

    public ManagementApiFactory Factory { get; } = new();

    public JobTestHost.RecordingPublisher Events { get; } = new();

    public FakeLoadStore Store { get; } = new();

    public FakeGeneratorGroupScaler Scaler { get; } = new();

    public FakeLoadMetrics Metrics { get; } = new();

    public LoadOptions Options { get; }

    /// <summary>The host's services, for seeding through the real scoped stores.</summary>
    public IServiceProvider Services => _app.Services;

    /// <summary>A client carrying <paramref name="token"/>, or anonymous when null.</summary>
    public HttpClient Client(string? token)
    {
        var client = _app.CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    public HttpClient Admin() => Client(ManagementApiFactory.AdminToken());

    /// <summary>An anonymous client presenting <paramref name="runToken"/> in the run-token header.</summary>
    public HttpClient Generator(string? runToken = Token)
    {
        var client = _app.CreateClient();
        if (runToken is not null)
        {
            client.DefaultRequestHeaders.Add(LoadEndpoints.TokenHeader, runToken);
        }

        return client;
    }

    /// <summary>
    /// Seed the target service in the manifest and <paramref name="instances"/> fleet
    /// instances with a fresh heartbeat that run it.
    /// </summary>
    public Task SeedTargetAsync(bool presence = false, int instances = 1, DateTimeOffset? heartbeatAt = null) =>
        Factory.WithDbAsync(async db =>
        {
            db.ServiceManifests.Add(new ServiceManifest
            {
                Name = Target,
                Image = "registry.example.com/svc-a",
                Tag = "v1",
                Port = 8080,
                DesiredStatus = "running",
                RealtimePresence = presence,
                IncludeInHealth = true,
                UpdatedBy = "seed",
                UpdatedAt = DateTimeOffset.UnixEpoch,
            });
            for (var i = 0; i < instances; i++)
            {
                db.InstanceStatus.Add(Instance($"i-0placeholder{i}", heartbeatAt ?? DateTimeOffset.UtcNow));
            }

            await db.SaveChangesAsync();
        });

    /// <summary>Seed rows directly on the Sqlite database (manifest, instances).</summary>
    public Task SeedDbAsync(params object[] rows) => Factory.WithDbAsync(async db =>
    {
        db.AddRange(rows);
        await db.SaveChangesAsync();
    });

    /// <summary>
    /// Seed a scenario that is ready to run: version 1 with its history row and a
    /// succeeded validation run (id 900) of that version.
    /// </summary>
    public LoadScenario SeedReadyScenario(string name = "tracker", int maxVus = 500, string paramsDefault = "{\"channel\":\"tracker\"}")
    {
        var scenario = Scenario(name, maxVus: maxVus, paramsDefault: paramsDefault);
        scenario.LastValidationRunId = 900;
        Store.Seed(scenario, Version(name, 1, scenario.Script));
        Store.Seed(Run(900, LoadRunStatus.Succeeded, scenario: name, validation: true, generators: 1));
        return scenario;
    }

    public ValueTask DisposeAsync() => Factory.DisposeAsync();

    public static InstanceStatus Instance(string id, DateTimeOffset heartbeatAt, string target = Target, string state = "running") => new()
    {
        InstanceId = id,
        GatewayVer = "test",
        IsLeader = false,
        Services = $"[{{\"name\":\"{target}\",\"digest\":null,\"state\":\"{state}\",\"startedAt\":null,\"restarts\":0}}]",
        HeartbeatAt = heartbeatAt,
    };

    public static LoadScenario Scenario(
        string name,
        string script = "export default function () {}",
        int maxVus = 500,
        string paramsDefault = "{}") => new()
    {
        Name = name,
        Repository = "example-org/example-app",
        Script = script,
        ScriptVersion = 1,
        Image = "gateway-job-runner",
        Tag = "latest",
        ParamsDefault = paramsDefault,
        MaxVusPerGenerator = maxVus,
        UpdatedBy = "seed",
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    public static LoadScenarioVersion Version(string scenario, int version, string script) => new()
    {
        Scenario = scenario,
        Version = version,
        Script = script,
        Sha256 = JobEndpoints.Sha256Hex(script),
        UpdatedBy = "seed",
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    public static LoadRun Run(
        long id,
        string status,
        string scenario = "tracker",
        int generators = 2,
        int scenarioVersion = 1,
        bool validation = false,
        string token = Token) => new()
    {
        Id = id,
        Scenario = scenario,
        ScenarioVersion = scenarioVersion,
        Target = Target,
        Validation = validation,
        Actor = "seed",
        Status = status,
        Generators = generators,
        VusPerGenerator = 100,
        RampSeconds = 60,
        HoldSeconds = 120,
        Params = "{\"channel\":\"tracker\"}",
        HardStopAt = DateTimeOffset.UnixEpoch.AddHours(1),
        TokenHash = LoadEndpoints.TokenHash(token),
        QueuedAt = DateTimeOffset.UnixEpoch,
        StartedAt = status == LoadRunStatus.Queued ? null : DateTimeOffset.UnixEpoch,
        FinishedAt = LoadRunStatus.IsTerminal(status) ? DateTimeOffset.UnixEpoch : null,
        Peaks = "{}",
        Timeline = "[]",
    };

    /// <summary>A stats line in the reporter's shape (docs/load.md section 4).</summary>
    public static string StatsLine(long runId, int index, DateTimeOffset at, int vus, string metrics) =>
        $"{{\"runId\":{runId},\"index\":{index},\"at\":\"{at.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}\",\"vus\":{vus},\"metrics\":{metrics}}}";

    public static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");
}
