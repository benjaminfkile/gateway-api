using System.Net.Http.Headers;
using System.Text;
using Gateway.Api.Data;
using Gateway.Api.Load;
using Gateway.Api.Management;
using Gateway.Api.RealTime;
using Gateway.Api.Resources;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Gateway.Api.Tests;

/// <summary>
/// A <see cref="ManagementApiFactory"/> for the resources endpoints: the three fake
/// scalers in place of the AWS ones, a <see cref="FakeLoadStore"/> (so a test can seed
/// a non-terminal run), a recording publisher for <c>ops:resources</c>, and fixed
/// <see cref="ResourcesOptions"/> and <see cref="LoadOptions"/>. Audit rows go to the
/// factory's Sqlite database through <see cref="EfResourceAuditStore"/>. Every name is
/// a placeholder.
/// </summary>
public sealed class ResourceTestHost : IAsyncDisposable
{
    public const string FleetGroup = "fleet-group-placeholder";
    public const string FleetTemplate = "fleet-template-placeholder";
    public const string GeneratorGroup = "load-generators-placeholder";
    public const string CacheGroup = "backplane-placeholder";
    public const string DatabaseInstance = "gateway-db-placeholder";

    private readonly WebApplicationFactory<Program> _app;

    public ResourceTestHost(ResourcesOptions? options = null, LoadOptions? load = null)
    {
        Options = options ?? Configured();
        Load = load ?? new LoadOptions { GeneratorGroup = GeneratorGroup };

        Scaler.Groups[FleetGroup] = new FleetGroupSize(Min: 2, Desired: 2, Max: 2, InService: 2);
        Scaler.Groups[GeneratorGroup] = new FleetGroupSize(Min: 0, Desired: 0, Max: 2, InService: 0);
        Scaler.SeedTemplate(FleetTemplate, 4, "t4g.small");
        Cache.Groups[CacheGroup] = new CacheReplicationGroup(CacheGroup, "cache.t4g.small", "available");
        Database.Instances[DatabaseInstance] = new DatabaseInstanceState(DatabaseInstance, "db.t4g.medium", "available", null, 40);

        _app = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IChannelEventPublisher>();
            services.AddSingleton<IChannelEventPublisher>(Events);

            services.RemoveAll<ILoadStore>();
            services.AddSingleton<ILoadStore>(LoadStore);

            services.RemoveAll<IFleetScaler>();
            services.AddSingleton<IFleetScaler>(Scaler);

            services.RemoveAll<ICacheScaler>();
            services.AddSingleton<ICacheScaler>(Cache);

            services.RemoveAll<IDatabaseScaler>();
            services.AddSingleton<IDatabaseScaler>(Database);

            services.RemoveAll<ResourcesOptions>();
            services.AddSingleton(Options);

            services.RemoveAll<LoadOptions>();
            services.AddSingleton(Load);
        }));
    }

    public ManagementApiFactory Factory { get; } = new();

    public JobTestHost.RecordingPublisher Events { get; } = new();

    public FakeLoadStore LoadStore { get; } = new();

    public FakeFleetScaler Scaler { get; } = new();

    public FakeCacheScaler Cache { get; } = new();

    public FakeDatabaseScaler Database { get; } = new();

    public ResourcesOptions Options { get; }

    public LoadOptions Load { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>Every resource configured, with placeholder names and allow lists.</summary>
    public static ResourcesOptions Configured(int fleetMaxSize = ResourcesOptions.DefaultFleetMaxSize) => new()
    {
        FleetGroup = FleetGroup,
        FleetTemplate = FleetTemplate,
        FleetClasses = ResourcesOptions.ParseList("t4g.small, m7g.large, c7g.xlarge"),
        FleetMaxSize = fleetMaxSize,
        CacheReplicationGroup = CacheGroup,
        CacheNodeTypes = ResourcesOptions.ParseList("cache.t4g.small,cache.m7g.large"),
        DatabaseInstance = DatabaseInstance,
        DatabaseClasses = ResourcesOptions.ParseList("db.t4g.micro,db.t4g.small,db.t4g.medium,db.m7g.large,db.x9.placeholder"),
    };

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

    /// <summary>The watcher the host registered, to tick by hand.</summary>
    public FleetRefreshWatcher Watcher() =>
        _app.Services.GetServices<IHostedService>().OfType<FleetRefreshWatcher>().Single();

    /// <summary>Every audit row on the Sqlite database, oldest first.</summary>
    public async Task<List<ResourceAudit>> AuditAsync()
    {
        using var scope = _app.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IResourceAuditStore>();
        return (await store.ListAsync(500)).Reverse().ToList();
    }

    /// <summary>Seed a non-terminal load run.</summary>
    public void SeedActiveRun() => LoadStore.Seed(LoadTestHost.Run(7, LoadRunStatus.Holding));

    /// <summary>The <c>ops:resources</c> events published so far.</summary>
    public IReadOnlyList<JobTestHost.RecordingPublisher.Published> ResourceEvents() =>
        Events.All.Where(e => e.Channel == ResourceEndpoints.OpsResourcesChannel).ToList();

    public static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    public ValueTask DisposeAsync() => Factory.DisposeAsync();
}
