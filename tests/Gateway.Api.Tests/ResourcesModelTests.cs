using System.Net;
using Amazon.AutoScaling;
using Amazon.AutoScaling.Model;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.ElastiCache;
using Amazon.ElastiCache.Model;
using Amazon.RDS;
using Amazon.RDS.Model;
using Gateway.Api.Data;
using Gateway.Api.Load;
using Gateway.Api.Management;
using Gateway.Api.Resources;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Api.Tests;

/// <summary>
/// The pieces under the Resources API: <see cref="ResourcesOptions"/> parsing, which
/// scaler the host registers for which keys, the null scalers, the AWS scalers'
/// requests against scripted SDK clients, the documented <c>max_connections</c> table,
/// the fleet group resolver, the IMDS tag source and the in-memory audit store.
/// </summary>
public class ResourcesModelTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 5, 2, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    // ---------- options ----------

    [Fact]
    public void Options_ParseEveryClassListOnce_IntoASet()
    {
        var options = ResourcesOptions.FromConfiguration(Config(
            (ResourcesOptions.FleetGroupKey, " fleet-group-placeholder "),
            (ResourcesOptions.FleetClassesKey, "m7g.large, c7g.xlarge,,m7g.large "),
            (ResourcesOptions.CacheNodeTypesKey, "cache.t4g.small"),
            (ResourcesOptions.DatabaseClassesKey, ""),
            (ResourcesOptions.FleetMaxSizeKey, "12")));

        Assert.Equal("fleet-group-placeholder", options.FleetGroup);
        Assert.Equal(new[] { "c7g.xlarge", "m7g.large" }, options.FleetClasses.OrderBy(c => c, StringComparer.Ordinal));
        Assert.Equal(new[] { "cache.t4g.small" }, options.CacheNodeTypes);
        Assert.Empty(options.DatabaseClasses);
        Assert.Equal(12, options.FleetMaxSize);
        Assert.True(options.FleetConfigured);
        Assert.False(options.CacheConfigured);
        Assert.False(options.DatabaseConfigured);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("many")]
    public void Options_FleetMaxSize_DefaultsTo50(string? value)
    {
        var pairs = value is null ? Array.Empty<(string, string)>() : new[] { (ResourcesOptions.FleetMaxSizeKey, value) };
        Assert.Equal(50, ResourcesOptions.FromConfiguration(Config(pairs)).FleetMaxSize);
    }

    // ---------- registration ----------

    [Fact]
    public async Task Host_RegistersTheNullScalers_WithNoKeys()
    {
        await using var factory = new ManagementApiFactory();
        using var scope = factory.Services.CreateScope();

        Assert.IsType<NullFleetScaler>(scope.ServiceProvider.GetRequiredService<IFleetScaler>());
        Assert.IsType<NullCacheScaler>(scope.ServiceProvider.GetRequiredService<ICacheScaler>());
        Assert.IsType<NullDatabaseScaler>(scope.ServiceProvider.GetRequiredService<IDatabaseScaler>());
        Assert.IsType<EfResourceAuditStore>(scope.ServiceProvider.GetRequiredService<IResourceAuditStore>());
    }

    [Theory]
    [InlineData(ResourcesOptions.FleetGroupKey)]
    [InlineData(ResourcesOptions.FleetTemplateKey)]
    [InlineData("Load:GeneratorGroup")]
    public async Task Host_RegistersTheAwsFleetScaler_WhenAGroupOrTemplateIsSet(string key)
    {
        await using var factory = new ManagementApiFactory();
        await using var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting(key, "placeholder-name");
            b.ConfigureTestServices(ScriptedClients);
        });
        using var scope = app.Services.CreateScope();

        Assert.IsType<AwsFleetScaler>(scope.ServiceProvider.GetRequiredService<IFleetScaler>());
        Assert.IsType<NullCacheScaler>(scope.ServiceProvider.GetRequiredService<ICacheScaler>());
    }

    [Fact]
    public async Task Host_RegistersTheAwsCacheAndDatabaseScalers_WhenTheirKeysAreSet()
    {
        await using var factory = new ManagementApiFactory();
        await using var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting(ResourcesOptions.CacheReplicationGroupKey, "backplane-placeholder");
            b.UseSetting(ResourcesOptions.DatabaseInstanceKey, "gateway-db-placeholder");
            b.ConfigureTestServices(ScriptedClients);
        });
        using var scope = app.Services.CreateScope();

        Assert.IsType<ElastiCacheScaler>(scope.ServiceProvider.GetRequiredService<ICacheScaler>());
        Assert.IsType<RdsDatabaseScaler>(scope.ServiceProvider.GetRequiredService<IDatabaseScaler>());
        Assert.IsType<NullFleetScaler>(scope.ServiceProvider.GetRequiredService<IFleetScaler>());
    }

    [Fact]
    public async Task Preflight_ReadsTheGeneratorGroupAndTheTemplate_ThroughTheAwsScaler()
    {
        var (autoScaling, asProxy) = AwsClientProxy<IAmazonAutoScaling>.Create();
        asProxy.On(nameof(IAmazonAutoScaling.DescribeAutoScalingGroupsAsync), _ => new DescribeAutoScalingGroupsResponse
        {
            AutoScalingGroups = new List<AutoScalingGroup>
            {
                new() { AutoScalingGroupName = "load-generators-placeholder", MinSize = 0, DesiredCapacity = 0, MaxSize = 2, Instances = new List<Amazon.AutoScaling.Model.Instance>() },
            },
        });
        var (ec2, ec2Proxy) = AwsClientProxy<IAmazonEC2>.Create();
        ec2Proxy.On(nameof(IAmazonEC2.DescribeLaunchTemplateVersionsAsync), _ => new DescribeLaunchTemplateVersionsResponse
        {
            LaunchTemplateVersions = new List<LaunchTemplateVersion>
            {
                new() { LaunchTemplateId = "lt-0placeholder", VersionNumber = 4, LaunchTemplateData = new ResponseLaunchTemplateData { InstanceType = InstanceType.T4gSmall } },
            },
        });

        await using var factory = new ManagementApiFactory();
        await using var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Load:GeneratorGroup", "load-generators-placeholder");
            b.UseSetting(ResourcesOptions.FleetTemplateKey, "fleet-template-placeholder");
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAmazonAutoScaling>();
                services.AddSingleton(autoScaling);
                services.RemoveAll<IAmazonEC2>();
                services.AddSingleton(ec2);
            });
        });

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ManagementApiFactory.AdminToken());
        var body = await client.GetStringAsync("/mgmt/load/preflight?scenario=tracker&target=svc-a&generators=1");
        var checks = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("checks").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c);

        Assert.Equal("green", checks["generator_group"].GetProperty("status").GetString());
        Assert.Equal("amber", checks["fleet_class"].GetProperty("status").GetString());
        Assert.Contains("t4g.small", checks["fleet_class"].GetProperty("message").GetString());
        Assert.Single(asProxy.Requests<DescribeAutoScalingGroupsRequest>(nameof(IAmazonAutoScaling.DescribeAutoScalingGroupsAsync)),
            r => r.AutoScalingGroupNames.SequenceEqual(new[] { "load-generators-placeholder" }));
        Assert.Contains(ec2Proxy.Requests<DescribeLaunchTemplateVersionsRequest>(nameof(IAmazonEC2.DescribeLaunchTemplateVersionsAsync)),
            r => r.LaunchTemplateName == "fleet-template-placeholder" && r.Versions.SequenceEqual(new[] { "$Default" }));
    }

    private static void ScriptedClients(IServiceCollection services)
    {
        services.RemoveAll<IAmazonAutoScaling>();
        services.AddSingleton(AwsClientProxy<IAmazonAutoScaling>.Create().Client);
        services.RemoveAll<IAmazonEC2>();
        services.AddSingleton(AwsClientProxy<IAmazonEC2>.Create().Client);
        services.RemoveAll<IAmazonElastiCache>();
        services.AddSingleton(AwsClientProxy<IAmazonElastiCache>.Create().Client);
        services.RemoveAll<IAmazonRDS>();
        services.AddSingleton(AwsClientProxy<IAmazonRDS>.Create().Client);
    }

    // ---------- null scalers ----------

    [Fact]
    public async Task NullCacheAndDatabaseScalers_RefuseEveryCall()
    {
        var cache = new NullCacheScaler();
        var database = new NullDatabaseScaler();
        var calls = new Func<Task>[]
        {
            () => cache.DescribeReplicationGroupAsync("c"),
            () => cache.ModifyNodeTypeAsync("c", "cache.m7g.large"),
            () => database.DescribeInstanceAsync("d"),
            () => database.ModifyInstanceClassAsync("d", "db.m7g.large"),
        };

        foreach (var call in calls)
        {
            await Assert.ThrowsAsync<NotSupportedException>(call);
        }
    }

    // ---------- AWS fleet scaler ----------

    [Fact]
    public async Task AwsFleetScaler_DescribesAGroup_CountingOnlyInService()
    {
        var (autoScaling, proxy) = AwsClientProxy<IAmazonAutoScaling>.Create();
        proxy.On(nameof(IAmazonAutoScaling.DescribeAutoScalingGroupsAsync), _ => new DescribeAutoScalingGroupsResponse
        {
            AutoScalingGroups = new List<AutoScalingGroup>
            {
                new()
                {
                    AutoScalingGroupName = "fleet-group-placeholder", MinSize = 2, DesiredCapacity = 3, MaxSize = 4,
                    Instances = new List<Amazon.AutoScaling.Model.Instance>
                    {
                        new() { LifecycleState = LifecycleState.InService },
                        new() { LifecycleState = LifecycleState.InService },
                        new() { LifecycleState = LifecycleState.Pending },
                    },
                },
            },
        });
        var scaler = new AwsFleetScaler(autoScaling, AwsClientProxy<IAmazonEC2>.Create().Client);

        Assert.Equal(new FleetGroupSize(2, 3, 4, 2), await scaler.DescribeGroupAsync("fleet-group-placeholder"));

        proxy.On(nameof(IAmazonAutoScaling.DescribeAutoScalingGroupsAsync), _ => new DescribeAutoScalingGroupsResponse { AutoScalingGroups = new List<AutoScalingGroup>() });
        Assert.Null(await scaler.DescribeGroupAsync("fleet-group-placeholder"));
    }

    [Fact]
    public async Task AwsFleetScaler_SetsSizesAndDesired()
    {
        var (autoScaling, proxy) = AwsClientProxy<IAmazonAutoScaling>.Create();
        proxy.On(nameof(IAmazonAutoScaling.UpdateAutoScalingGroupAsync), _ => null)
            .On(nameof(IAmazonAutoScaling.SetDesiredCapacityAsync), _ => null);
        var scaler = new AwsFleetScaler(autoScaling, AwsClientProxy<IAmazonEC2>.Create().Client);

        await scaler.SetGroupSizeAsync("fleet-group-placeholder", 4, 4, 5);
        await scaler.SetDesiredAsync("load-generators-placeholder", 0);

        var update = Assert.Single(proxy.Requests<UpdateAutoScalingGroupRequest>(nameof(IAmazonAutoScaling.UpdateAutoScalingGroupAsync)));
        Assert.Equal(("fleet-group-placeholder", 4, 4, 5), (update.AutoScalingGroupName, update.MinSize, update.DesiredCapacity, update.MaxSize));
        var desired = Assert.Single(proxy.Requests<SetDesiredCapacityRequest>(nameof(IAmazonAutoScaling.SetDesiredCapacityAsync)));
        Assert.Equal(("load-generators-placeholder", 0), (desired.AutoScalingGroupName, desired.DesiredCapacity));
    }

    [Fact]
    public async Task AwsFleetScaler_CreatesAVersionFromDefault_WithOnlyInstanceType()
    {
        var (ec2, proxy) = AwsClientProxy<IAmazonEC2>.Create();
        proxy.On(nameof(IAmazonEC2.CreateLaunchTemplateVersionAsync), _ => new CreateLaunchTemplateVersionResponse
        {
            LaunchTemplateVersion = new LaunchTemplateVersion { VersionNumber = 5 },
        }).On(nameof(IAmazonEC2.ModifyLaunchTemplateAsync), _ => null);
        var scaler = new AwsFleetScaler(AwsClientProxy<IAmazonAutoScaling>.Create().Client, ec2);

        Assert.Equal(5, await scaler.CreateLaunchTemplateVersionAsync("fleet-template-placeholder", "m7g.large"));
        await scaler.SetDefaultLaunchTemplateVersionAsync("lt-0placeholder", 5);

        var create = Assert.Single(proxy.Requests<CreateLaunchTemplateVersionRequest>(nameof(IAmazonEC2.CreateLaunchTemplateVersionAsync)));
        Assert.Equal("fleet-template-placeholder", create.LaunchTemplateName);
        Assert.Null(create.LaunchTemplateId);
        Assert.Equal("$Default", create.SourceVersion);
        Assert.Equal("m7g.large", create.LaunchTemplateData.InstanceType.Value);
        // Every other field of the request data is as a fresh instance has it, so
        // SourceVersion supplies it from $Default.
        var fresh = new RequestLaunchTemplateData();
        var setFields = typeof(RequestLaunchTemplateData).GetProperties()
            .Where(p => p.Name != nameof(RequestLaunchTemplateData.InstanceType))
            .Select(p => (p.Name, Value: p.GetValue(create.LaunchTemplateData), Fresh: p.GetValue(fresh)))
            .Where(p => !Equals(p.Value, p.Fresh) && !(p.Value is System.Collections.ICollection { Count: 0 }))
            .Select(p => p.Name)
            .ToList();
        Assert.Empty(setFields);

        var modify = Assert.Single(proxy.Requests<ModifyLaunchTemplateRequest>(nameof(IAmazonEC2.ModifyLaunchTemplateAsync)));
        Assert.Equal(("lt-0placeholder", "5"), (modify.LaunchTemplateId, modify.DefaultVersion));
    }

    [Fact]
    public async Task AwsFleetScaler_DescribesTheDefault_AndReadsAMissingVersionAsNull()
    {
        var (ec2, proxy) = AwsClientProxy<IAmazonEC2>.Create();
        proxy.On(nameof(IAmazonEC2.DescribeLaunchTemplateVersionsAsync), request =>
        {
            var versions = ((DescribeLaunchTemplateVersionsRequest)request!).Versions;
            if (versions.SequenceEqual(new[] { "99" }))
            {
                throw new AmazonEC2Exception("no such version") { ErrorCode = "InvalidLaunchTemplateId.VersionNotFound" };
            }

            return new DescribeLaunchTemplateVersionsResponse
            {
                LaunchTemplateVersions = new List<LaunchTemplateVersion>
                {
                    new()
                    {
                        LaunchTemplateId = "lt-0placeholder", VersionNumber = 4, VersionDescription = "placeholder",
                        LaunchTemplateData = new ResponseLaunchTemplateData { InstanceType = InstanceType.T4gSmall },
                    },
                },
            };
        });
        var scaler = new AwsFleetScaler(AwsClientProxy<IAmazonAutoScaling>.Create().Client, ec2);

        Assert.Equal(new FleetLaunchTemplate("lt-0placeholder", 4, "t4g.small", "placeholder"), await scaler.DescribeLaunchTemplateAsync("fleet-template-placeholder"));
        Assert.Equal(new FleetTemplateVersion(4, "t4g.small", "placeholder"), await scaler.DescribeLaunchTemplateVersionAsync("fleet-template-placeholder", 4));
        Assert.Null(await scaler.DescribeLaunchTemplateVersionAsync("fleet-template-placeholder", 99));
    }

    [Fact]
    public async Task AwsFleetScaler_StartsARefreshWithTheDeployPreferences_AndDescribesById()
    {
        var (autoScaling, proxy) = AwsClientProxy<IAmazonAutoScaling>.Create();
        proxy.On(nameof(IAmazonAutoScaling.StartInstanceRefreshAsync), _ => new StartInstanceRefreshResponse { InstanceRefreshId = "refresh-placeholder" })
            .On(nameof(IAmazonAutoScaling.CancelInstanceRefreshAsync), _ => null)
            .On(nameof(IAmazonAutoScaling.DescribeInstanceRefreshesAsync), _ => new DescribeInstanceRefreshesResponse
            {
                InstanceRefreshes = new List<InstanceRefresh>
                {
                    new() { InstanceRefreshId = "refresh-placeholder", Status = InstanceRefreshStatus.InProgress, PercentageComplete = 40 },
                },
            });
        var scaler = new AwsFleetScaler(autoScaling, AwsClientProxy<IAmazonEC2>.Create().Client);

        Assert.Equal("refresh-placeholder", await scaler.StartInstanceRefreshAsync("fleet-group-placeholder", FleetRefreshPreferences.Deploy));
        var refreshes = await scaler.DescribeInstanceRefreshesAsync("fleet-group-placeholder", "refresh-placeholder");
        await scaler.CancelInstanceRefreshAsync("fleet-group-placeholder");

        var start = Assert.Single(proxy.Requests<StartInstanceRefreshRequest>(nameof(IAmazonAutoScaling.StartInstanceRefreshAsync)));
        Assert.Equal((100, 150, 300), (start.Preferences.MinHealthyPercentage, start.Preferences.MaxHealthyPercentage, start.Preferences.InstanceWarmup));
        Assert.Equal(new[] { new FleetRefresh("refresh-placeholder", "InProgress", 40) }, refreshes);
        var describe = Assert.Single(proxy.Requests<DescribeInstanceRefreshesRequest>(nameof(IAmazonAutoScaling.DescribeInstanceRefreshesAsync)));
        Assert.Equal(new[] { "refresh-placeholder" }, describe.InstanceRefreshIds);
    }

    [Theory]
    [InlineData("Pending", false)]
    [InlineData("InProgress", false)]
    [InlineData("Cancelling", false)]
    [InlineData("RollbackInProgress", false)]
    [InlineData("Baking", false)]
    [InlineData("Successful", true)]
    [InlineData("Failed", true)]
    [InlineData("Cancelled", true)]
    [InlineData("RollbackSuccessful", true)]
    [InlineData("RollbackFailed", true)]
    public void FleetRefresh_IsTerminal(string status, bool terminal) =>
        Assert.Equal(terminal, new FleetRefresh("r", status, null).IsTerminal);

    // ---------- AWS cache and database scalers ----------

    [Fact]
    public async Task ElastiCacheScaler_DescribesAndModifiesImmediately()
    {
        var (client, proxy) = AwsClientProxy<IAmazonElastiCache>.Create();
        proxy.On(nameof(IAmazonElastiCache.DescribeReplicationGroupsAsync), _ => new DescribeReplicationGroupsResponse
        {
            ReplicationGroups = new List<ReplicationGroup> { new() { ReplicationGroupId = "backplane-placeholder", CacheNodeType = "cache.t4g.small", Status = "available" } },
        }).On(nameof(IAmazonElastiCache.ModifyReplicationGroupAsync), _ => null);
        var scaler = new ElastiCacheScaler(client);

        Assert.Equal(new CacheReplicationGroup("backplane-placeholder", "cache.t4g.small", "available"), await scaler.DescribeReplicationGroupAsync("backplane-placeholder"));
        await scaler.ModifyNodeTypeAsync("backplane-placeholder", "cache.m7g.large");

        var modify = Assert.Single(proxy.Requests<ModifyReplicationGroupRequest>(nameof(IAmazonElastiCache.ModifyReplicationGroupAsync)));
        Assert.Equal(("backplane-placeholder", "cache.m7g.large", true), (modify.ReplicationGroupId, modify.CacheNodeType, modify.ApplyImmediately));

        proxy.On(nameof(IAmazonElastiCache.DescribeReplicationGroupsAsync), _ => throw new ReplicationGroupNotFoundException("missing"));
        Assert.Null(await scaler.DescribeReplicationGroupAsync("backplane-placeholder"));
    }

    [Theory]
    [InlineData("800", 800)]
    [InlineData("LEAST({DBInstanceClassMemory/9531392},5000)", null)]
    public async Task RdsDatabaseScaler_ReadsMaxConnectionsFromTheParameterGroup_AndInUseFromHeadroom(string value, int? expected)
    {
        var (client, proxy) = AwsClientProxy<IAmazonRDS>.Create();
        proxy.On(nameof(IAmazonRDS.DescribeDBInstancesAsync), _ => new DescribeDBInstancesResponse
        {
            DBInstances = new List<DBInstance>
            {
                new()
                {
                    DBInstanceIdentifier = "gateway-db-placeholder", DBInstanceClass = "db.t4g.medium", DBInstanceStatus = "available",
                    DBParameterGroups = new List<DBParameterGroupStatus> { new() { DBParameterGroupName = "params-placeholder" } },
                },
            },
        }).On(nameof(IAmazonRDS.DescribeDBParametersAsync), request =>
            ((DescribeDBParametersRequest)request!).Marker is null
                ? new DescribeDBParametersResponse { Parameters = new List<Amazon.RDS.Model.Parameter> { new() { ParameterName = "work_mem", ParameterValue = "4096" } }, Marker = "page-2" }
                : new DescribeDBParametersResponse { Parameters = new List<Amazon.RDS.Model.Parameter> { new() { ParameterName = "max_connections", ParameterValue = value } } })
          .On(nameof(IAmazonRDS.ModifyDBInstanceAsync), _ => null);
        var headroom = new FakeDatabaseHeadroom { Connections = new DatabaseConnections(450, 37) };
        var scaler = new RdsDatabaseScaler(client, headroom, NullLogger<RdsDatabaseScaler>.Instance);

        var state = await scaler.DescribeInstanceAsync("gateway-db-placeholder");
        await scaler.ModifyInstanceClassAsync("gateway-db-placeholder", "db.m7g.large");

        Assert.Equal(new DatabaseInstanceState("gateway-db-placeholder", "db.t4g.medium", "available", expected, 37), state);
        Assert.Equal(2, proxy.Requests<DescribeDBParametersRequest>(nameof(IAmazonRDS.DescribeDBParametersAsync)).Count);
        var modify = Assert.Single(proxy.Requests<ModifyDBInstanceRequest>(nameof(IAmazonRDS.ModifyDBInstanceAsync)));
        Assert.Equal(("gateway-db-placeholder", "db.m7g.large", true), (modify.DBInstanceIdentifier, modify.DBInstanceClass, modify.ApplyImmediately));
    }

    // ---------- documented max_connections ----------

    [Theory]
    [InlineData("db.t4g.micro", 112)]
    [InlineData("db.t4g.small", 225)]
    [InlineData("db.t4g.medium", 450)]
    [InlineData("db.m7g.large", 901)]
    [InlineData("db.r7g.large", 1802)]
    [InlineData("db.m7g.2xlarge", 3604)]
    [InlineData("db.r7g.2xlarge", 5000)]
    public void DatabaseClassConnections_FollowTheRdsDefault(string instanceClass, int max) =>
        Assert.Equal(max, DatabaseClassConnections.For(instanceClass));

    [Fact]
    public void DatabaseClassConnections_UnknownIsNull() =>
        Assert.Null(DatabaseClassConnections.For("db.x9.placeholder"));

    // ---------- fleet group resolver and tag source ----------

    [Fact]
    public async Task Resolver_PrefersTheKey_ThenTheTag_OnlyWhenTheTemplateIsSet()
    {
        var source = new FakeFleetGroupSource { Group = "tagged-group-placeholder" };
        var clock = new ManualClock();

        var keyed = new FleetGroupResolver(new ResourcesOptions { FleetGroup = "fleet-group-placeholder" }, source, clock);
        Assert.Equal("fleet-group-placeholder", await keyed.GetAsync());
        Assert.Equal(0, source.Reads);

        var bare = new FleetGroupResolver(new ResourcesOptions(), source, clock);
        Assert.Null(await bare.GetAsync());
        Assert.Equal(0, source.Reads);

        var tagged = new FleetGroupResolver(new ResourcesOptions { FleetTemplate = "fleet-template-placeholder" }, source, clock);
        Assert.Equal("tagged-group-placeholder", await tagged.GetAsync());
        Assert.Equal("tagged-group-placeholder", await tagged.GetAsync());
        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public async Task Resolver_RetriesAFailedTagRead_AfterFiveMinutes()
    {
        var source = new FakeFleetGroupSource();
        var clock = new ManualClock();
        var resolver = new FleetGroupResolver(new ResourcesOptions { FleetTemplate = "fleet-template-placeholder" }, source, clock);

        Assert.Null(await resolver.GetAsync());
        Assert.Null(await resolver.GetAsync());
        Assert.Equal(1, source.Reads);

        source.Group = "tagged-group-placeholder";
        clock.Now += FleetGroupResolver.RetryAfter;
        Assert.Equal("tagged-group-placeholder", await resolver.GetAsync());
        Assert.Equal(2, source.Reads);
    }

    private sealed class ImdsHandler : HttpMessageHandler
    {
        public HttpStatusCode TagStatus { get; set; } = HttpStatusCode.OK;

        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            if (request.Method == HttpMethod.Put)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("token-placeholder") });
            }

            Assert.Equal("token-placeholder", request.Headers.GetValues("X-aws-ec2-metadata-token").Single());
            return Task.FromResult(new HttpResponseMessage(TagStatus) { Content = new StringContent("tagged-group-placeholder\n") });
        }
    }

    private sealed class Clients : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public Clients(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false) { BaseAddress = new Uri("http://imds.invalid/") };
    }

    [Fact]
    public async Task ImdsSource_ReadsTheGroupTag_WithASessionToken()
    {
        var handler = new ImdsHandler();
        var source = new ImdsFleetGroupSource(new Clients(handler));

        Assert.Equal("tagged-group-placeholder", await source.TryGetAsync());
        Assert.Equal(new[] { "PUT /latest/api/token", "GET /" + ImdsFleetGroupSource.TagPath }, handler.Requests);

        handler.TagStatus = HttpStatusCode.NotFound;
        Assert.Null(await source.TryGetAsync());
    }

    // ---------- in-memory audit store ----------

    [Fact]
    public async Task InMemoryAudit_ListsNewestFirst_FiltersAndKeepsTheNewest()
    {
        var store = new InMemoryResourceAuditStore();
        for (var i = 0; i < InMemoryResourceAuditStore.Capacity + 5; i++)
        {
            await store.AddAsync(new ResourceAudit { Actor = "a", Resource = i % 2 == 0 ? "fleet" : "cache", Action = "size", At = DateTimeOffset.UnixEpoch });
        }

        var newest = await store.ListAsync(3);
        Assert.Equal(new long[] { 505, 504, 503 }, newest.Select(r => r.Id));
        Assert.All(await store.ListAsync(10, "cache"), r => Assert.Equal("cache", r.Resource));
        Assert.Equal(InMemoryResourceAuditStore.Capacity, (await store.ListAsync(1000)).Count);
    }
}
