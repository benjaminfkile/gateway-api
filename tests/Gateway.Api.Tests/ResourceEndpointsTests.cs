using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.Runtime;
using Gateway.Api.Data;
using Gateway.Api.Load;
using Gateway.Api.Management;
using Gateway.Api.Resources;

namespace Gateway.Api.Tests;

/// <summary>
/// The <c>/mgmt/resources</c> endpoints of docs/load.md section 9 through the
/// management factory with the fake scalers: the read shape, the unconfigured,
/// allow list, run_active and refresh_in_progress answers, the fleet size and class
/// sequences, rollback, the cache note, the database confirm and downgrade rule, and
/// the audit rows and <c>ops:resources</c> events every mutation leaves.
/// </summary>
public class ResourceEndpointsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static TheoryData<string, string> Mutations => new()
    {
        { "/mgmt/resources/fleet/size", """{"size":3}""" },
        { "/mgmt/resources/fleet/class", """{"instanceClass":"m7g.large"}""" },
        { "/mgmt/resources/fleet/class/rollback", """{"templateVersion":4}""" },
        { "/mgmt/resources/cache/node-type", """{"nodeType":"cache.m7g.large"}""" },
        { "/mgmt/resources/database/class", """{"instanceClass":"db.m7g.large","confirm":"gateway-db-placeholder"}""" },
    };

    private static JsonElement Data(JobTestHost.RecordingPublisher.Published p) => JsonSerializer.SerializeToElement(p.Data, Web);

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PutAsync(ResourceTestHost host, string path, string json)
    {
        var response = await host.Admin().PutAsync(path, ResourceTestHost.Json(json));
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static IEnumerable<string> Mutating(FakeFleetScaler scaler) =>
        scaler.CallLog.Where(c => !c.StartsWith("describe", StringComparison.Ordinal));

    // ---------- GET /mgmt/resources ----------

    [Fact]
    public async Task Get_ReportsEveryBlock()
    {
        await using var host = new ResourceTestHost();

        var body = await host.Client(ManagementApiFactory.DeployToken()).GetFromJsonAsync<JsonElement>("/mgmt/resources");

        Assert.False(body.GetProperty("runActive").GetBoolean());

        var fleet = body.GetProperty("fleet");
        Assert.Equal(ResourceTestHost.FleetGroup, fleet.GetProperty("group").GetString());
        Assert.Equal(2, fleet.GetProperty("min").GetInt32());
        Assert.Equal(2, fleet.GetProperty("desired").GetInt32());
        Assert.Equal(2, fleet.GetProperty("max").GetInt32());
        Assert.Equal(2, fleet.GetProperty("inService").GetInt32());
        Assert.Equal("t4g.small", fleet.GetProperty("instanceClass").GetString());
        Assert.Equal(4, fleet.GetProperty("templateVersion").GetInt64());
        Assert.False(fleet.GetProperty("refreshInProgress").GetBoolean());
        Assert.Equal(new[] { "c7g.xlarge", "m7g.large", "t4g.small" }, fleet.GetProperty("classes").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(50, fleet.GetProperty("maxSize").GetInt32());

        var generators = body.GetProperty("generators");
        Assert.Equal(ResourceTestHost.GeneratorGroup, generators.GetProperty("group").GetString());
        Assert.Equal(0, generators.GetProperty("desired").GetInt32());
        Assert.Equal(0, generators.GetProperty("inService").GetInt32());
        Assert.Equal(2, generators.GetProperty("max").GetInt32());

        var cache = body.GetProperty("cache");
        Assert.Equal(ResourceTestHost.CacheGroup, cache.GetProperty("id").GetString());
        Assert.Equal("cache.t4g.small", cache.GetProperty("nodeType").GetString());
        Assert.Equal("available", cache.GetProperty("status").GetString());

        var database = body.GetProperty("database");
        Assert.Equal(ResourceTestHost.DatabaseInstance, database.GetProperty("id").GetString());
        Assert.Equal("db.t4g.medium", database.GetProperty("instanceClass").GetString());
        Assert.Equal("available", database.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, database.GetProperty("maxConnections").ValueKind);
        Assert.Equal(40, database.GetProperty("connectionsInUse").GetInt32());
        Assert.Equal(450, database.GetProperty("documentedMaxConnections").GetInt32());
    }

    [Fact]
    public async Task Get_EveryBlockIsNull_WhenUnconfigured()
    {
        await using var host = new ResourceTestHost(new ResourcesOptions(), new LoadOptions());

        var body = await host.Admin().GetFromJsonAsync<JsonElement>("/mgmt/resources");

        foreach (var block in new[] { "fleet", "generators", "cache", "database" })
        {
            Assert.Equal(JsonValueKind.Null, body.GetProperty(block).ValueKind);
        }
    }

    [Fact]
    public async Task Get_ReportsARefreshInFlight_AndAnActiveRun()
    {
        await using var host = new ResourceTestHost();
        host.Scaler.SetRefresh(ResourceTestHost.FleetGroup, "refresh-placeholder-a", "InProgress", 40);
        host.SeedActiveRun();

        var body = await host.Admin().GetFromJsonAsync<JsonElement>("/mgmt/resources");
        Assert.True(body.GetProperty("fleet").GetProperty("refreshInProgress").GetBoolean());
        Assert.True(body.GetProperty("runActive").GetBoolean());

        var refresh = await host.Admin().GetFromJsonAsync<JsonElement>("/mgmt/resources/fleet/refresh");
        Assert.Equal("refresh-placeholder-a", refresh.GetProperty("refreshId").GetString());
        Assert.Equal("InProgress", refresh.GetProperty("status").GetString());
        Assert.Equal(40, refresh.GetProperty("percentComplete").GetInt32());
        Assert.True(refresh.GetProperty("inProgress").GetBoolean());
    }

    [Fact]
    public async Task GetRefresh_NoneYet_And503WhenTheFleetIsUnconfigured()
    {
        await using var host = new ResourceTestHost();
        var none = await host.Admin().GetFromJsonAsync<JsonElement>("/mgmt/resources/fleet/refresh");
        Assert.Equal(JsonValueKind.Null, none.GetProperty("refreshId").ValueKind);
        Assert.False(none.GetProperty("inProgress").GetBoolean());

        await using var bare = new ResourceTestHost(new ResourcesOptions());
        var response = await bare.Admin().GetAsync("/mgmt/resources/fleet/refresh");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Get_ReportsAScalerError_InTheBlock()
    {
        await using var host = new ResourceTestHost();
        host.Scaler.Failing.Add(ResourceTestHost.FleetGroup);

        var body = await host.Admin().GetFromJsonAsync<JsonElement>("/mgmt/resources");

        var fleet = body.GetProperty("fleet");
        Assert.Equal(NullFleetScaler.NotConfiguredMessage, fleet.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, fleet.GetProperty("desired").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("cache").GetProperty("error").ValueKind);
    }

    // ---------- the shared refusals ----------

    [Theory]
    [MemberData(nameof(Mutations))]
    public async Task Mutation_503_WhenTheResourceIsUnconfigured(string path, string json)
    {
        await using var host = new ResourceTestHost(new ResourcesOptions());

        var (status, body) = await PutAsync(host, path, json);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("resource_unconfigured", body.GetProperty("error").GetString());
        Assert.Empty(Mutating(host.Scaler));
        Assert.Empty(await host.AuditAsync());
    }

    [Fact]
    public async Task FleetClass_503_WhenOnlyTheTemplateIsUnset()
    {
        var options = ResourceTestHost.Configured();
        await using var host = new ResourceTestHost(new ResourcesOptions
        {
            FleetGroup = options.FleetGroup,
            FleetClasses = options.FleetClasses,
        });

        var (status, _) = await PutAsync(host, "/mgmt/resources/fleet/class", """{"instanceClass":"m7g.large"}""");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);

        // The size needs only the group.
        var (size, _) = await PutAsync(host, "/mgmt/resources/fleet/size", """{"size":3}""");
        Assert.Equal(HttpStatusCode.Accepted, size);
    }

    [Theory]
    [InlineData("/mgmt/resources/fleet/class", """{"instanceClass":"m5.24xlarge"}""", "instanceClass")]
    [InlineData("/mgmt/resources/fleet/class", """{}""", "instanceClass")]
    [InlineData("/mgmt/resources/cache/node-type", """{"nodeType":"cache.r7g.16xlarge"}""", "nodeType")]
    [InlineData("/mgmt/resources/database/class", """{"instanceClass":"db.r7g.16xlarge","confirm":"gateway-db-placeholder"}""", "instanceClass")]
    public async Task Mutation_400_ClassNotAllowed_OutsideTheAllowList(string path, string json, string field)
    {
        await using var host = new ResourceTestHost();

        var (status, body) = await PutAsync(host, path, json);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("class_not_allowed", body.GetProperty("error").GetString());
        Assert.Equal(field, body.GetProperty("field").GetString());
        Assert.Empty(Mutating(host.Scaler));
        Assert.DoesNotContain(host.Cache.Calls, c => c.StartsWith("modify", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Database.Calls, c => c.StartsWith("modify", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Mutations))]
    public async Task Mutation_409_RunActive_WhileALoadRunIsNonTerminal(string path, string json)
    {
        await using var host = new ResourceTestHost();
        host.SeedActiveRun();

        var (status, body) = await PutAsync(host, path, json);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("run_active", body.GetProperty("error").GetString());
        Assert.Empty(Mutating(host.Scaler));
        Assert.DoesNotContain(host.Cache.Calls, c => c.StartsWith("modify", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Database.Calls, c => c.StartsWith("modify", StringComparison.Ordinal));
        Assert.Empty(await host.AuditAsync());
        Assert.Empty(host.ResourceEvents());
    }

    [Fact]
    public async Task Mutation_Allowed_AfterTheRunEnds()
    {
        await using var host = new ResourceTestHost();
        host.LoadStore.Seed(LoadTestHost.Run(7, LoadRunStatus.Succeeded));

        var (status, _) = await PutAsync(host, "/mgmt/resources/fleet/size", """{"size":3}""");

        Assert.Equal(HttpStatusCode.Accepted, status);
    }

    [Theory]
    [InlineData("/mgmt/resources/fleet/size", """{"size":3}""")]
    [InlineData("/mgmt/resources/fleet/class", """{"instanceClass":"m7g.large"}""")]
    [InlineData("/mgmt/resources/fleet/class/rollback", """{"templateVersion":4}""")]
    public async Task Fleet_409_RefreshInProgress(string path, string json)
    {
        await using var host = new ResourceTestHost();
        host.Scaler.SetRefresh(ResourceTestHost.FleetGroup, "refresh-placeholder-ci", "InProgress", 10);

        var (status, body) = await PutAsync(host, path, json);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("refresh_in_progress", body.GetProperty("error").GetString());
        Assert.Equal("refresh-placeholder-ci", body.GetProperty("refreshId").GetString());
        Assert.Empty(Mutating(host.Scaler));
    }

    [Fact]
    public async Task Mutation_502_OnAnAwsError()
    {
        await using var host = new ResourceTestHost();
        host.Scaler.StartRefreshError = new AmazonServiceException("placeholder failure");

        var (status, body) = await PutAsync(host, "/mgmt/resources/fleet/class", """{"instanceClass":"m7g.large"}""");

        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Equal("aws_error", body.GetProperty("error").GetString());
        Assert.Equal(5, body.GetProperty("templateVersion").GetInt64());
        Assert.Contains("placeholder failure", body.GetProperty("detail").GetString());

        // The max went up for the start and came straight back down.
        Assert.Equal(new FleetGroupSize(2, 2, 2, 2), host.Scaler.Groups[ResourceTestHost.FleetGroup]);
        var row = Assert.Single(await host.AuditAsync());
        Assert.Contains("placeholder failure", row.Detail);
        Assert.Null(ResourceEndpoints.ReadRefreshDetail(row));
    }

    // ---------- fleet size ----------

    [Theory]
    [InlineData("""{"size":0}""")]
    [InlineData("""{"size":51}""")]
    [InlineData("""{}""")]
    public async Task FleetSize_400_OutsideOneToTheMax(string json)
    {
        await using var host = new ResourceTestHost();

        var (status, body) = await PutAsync(host, "/mgmt/resources/fleet/size", json);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("size", body.GetProperty("field").GetString());
    }

    [Fact]
    public async Task FleetSize_HonoursTheConfiguredMax()
    {
        await using var host = new ResourceTestHost(ResourceTestHost.Configured(fleetMaxSize: 6));

        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(host, "/mgmt/resources/fleet/size", """{"size":7}""")).Status);
        Assert.Equal(HttpStatusCode.Accepted, (await PutAsync(host, "/mgmt/resources/fleet/size", """{"size":6}""")).Status);
    }

    [Fact]
    public async Task FleetSize_SetsMinDesiredAndMaxTogether_AuditsAndPublishes()
    {
        await using var host = new ResourceTestHost();

        var (status, body) = await PutAsync(host, "/mgmt/resources/fleet/size", """{"size":4}""");

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal(4, body.GetProperty("min").GetInt32());
        Assert.Equal(4, body.GetProperty("desired").GetInt32());
        Assert.Equal(4, body.GetProperty("max").GetInt32());
        Assert.Equal(new[] { $"set-group-size {ResourceTestHost.FleetGroup} 4 4 4" }, Mutating(host.Scaler));

        var row = Assert.Single(await host.AuditAsync());
        Assert.Equal(("alice-admin", "fleet", "size"), (row.Actor, row.Resource, row.Action));
        Assert.Equal("min 2, desired 2, max 2", row.Before);
        Assert.Equal("min 4, desired 4, max 4", row.After);

        var change = Data(Assert.Single(host.ResourceEvents(), e => e.Event == "change"));
        Assert.Equal("fleet", change.GetProperty("resource").GetString());
        Assert.Equal("size", change.GetProperty("action").GetString());
        Assert.Equal("alice-admin", change.GetProperty("by").GetString());
        Assert.Equal("min 2, desired 2, max 2", change.GetProperty("before").GetString());
        Assert.Equal("min 4, desired 4, max 4", change.GetProperty("after").GetString());
    }

    // ---------- fleet class ----------

    [Fact]
    public async Task FleetClass_CreatesAVersionFromDefault_SetsItDefault_ThenRaisesMaxAndStartsTheRefresh()
    {
        await using var host = new ResourceTestHost();

        var (status, body) = await PutAsync(host, "/mgmt/resources/fleet/class", """{"instanceClass":"m7g.large"}""");

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal(5, body.GetProperty("templateVersion").GetInt64());
        Assert.Equal("refresh-placeholder-1", body.GetProperty("refreshId").GetString());

        Assert.Equal(
            new[]
            {
                $"create-template-version {ResourceTestHost.FleetTemplate} from $Default InstanceType=m7g.large",
                $"set-default-version {ResourceTestHost.FleetTemplate} 5",
                $"set-group-size {ResourceTestHost.FleetGroup} 2 2 3",
                $"start-refresh {ResourceTestHost.FleetGroup} min-healthy 100 max-healthy 150 warmup 300",
            },
            Mutating(host.Scaler));
        Assert.Equal(5, host.Scaler.Templates[ResourceTestHost.FleetTemplate].DefaultVersion);
        Assert.Equal("m7g.large", host.Scaler.Templates[ResourceTestHost.FleetTemplate].InstanceType);

        var row = Assert.Single(await host.AuditAsync());
        Assert.Equal(("fleet", "class"), (row.Resource, row.Action));
        Assert.Equal("t4g.small (version 4)", row.Before);
        Assert.Equal("m7g.large (version 5)", row.After);
        var detail = ResourceEndpoints.ReadRefreshDetail(row);
        Assert.NotNull(detail);
        Assert.Equal(("refresh-placeholder-1", 2, 5L, 4L), (detail!.RefreshId, detail.RestoreMax, detail.TemplateVersion, detail.PreviousVersion));

        var events = host.ResourceEvents();
        Assert.Contains(events, e => e.Event == "change" && Data(e).GetProperty("action").GetString() == "class");
        var refresh = Data(Assert.Single(events, e => e.Event == "refresh"));
        Assert.Equal("refresh-placeholder-1", refresh.GetProperty("refreshId").GetString());
    }

    [Fact]
    public async Task FleetRollback_SetsAnExistingVersionDefault_AndStartsARefresh()
    {
        await using var host = new ResourceTestHost();
        await PutAsync(host, "/mgmt/resources/fleet/class", """{"instanceClass":"m7g.large"}""");
        host.Scaler.SetRefresh(ResourceTestHost.FleetGroup, "refresh-placeholder-1", "Successful", 100);
        host.Scaler.Groups[ResourceTestHost.FleetGroup] = new FleetGroupSize(2, 2, 2, 2);
        host.Scaler.Calls.Clear();

        var (status, body) = await PutAsync(host, "/mgmt/resources/fleet/class/rollback", """{"templateVersion":4}""");

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal(4, body.GetProperty("templateVersion").GetInt64());
        Assert.Equal("refresh-placeholder-2", body.GetProperty("refreshId").GetString());
        Assert.Equal(
            new[]
            {
                $"set-default-version {ResourceTestHost.FleetTemplate} 4",
                $"set-group-size {ResourceTestHost.FleetGroup} 2 2 3",
                $"start-refresh {ResourceTestHost.FleetGroup} min-healthy 100 max-healthy 150 warmup 300",
            },
            Mutating(host.Scaler));
        Assert.Equal("t4g.small", host.Scaler.Templates[ResourceTestHost.FleetTemplate].InstanceType);

        var row = (await host.AuditAsync()).Last();
        Assert.Equal(("class_rollback", "m7g.large (version 5)", "t4g.small (version 4)"), (row.Action, row.Before, row.After));
    }

    [Theory]
    [InlineData("""{"templateVersion":99}""", "version_not_found")]
    [InlineData("""{}""", null)]
    [InlineData("""{"templateVersion":0}""", null)]
    public async Task FleetRollback_400_ForAVersionThatDoesNotExist(string json, string? error)
    {
        await using var host = new ResourceTestHost();

        var (status, body) = await PutAsync(host, "/mgmt/resources/fleet/class/rollback", json);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("templateVersion", body.GetProperty("field").GetString());
        if (error is not null)
        {
            Assert.Equal(error, body.GetProperty("error").GetString());
        }

        Assert.Empty(Mutating(host.Scaler));
    }

    // ---------- cache ----------

    [Fact]
    public async Task CacheNodeType_Modifies_AndCarriesTheDegradationNote()
    {
        await using var host = new ResourceTestHost();

        var (status, body) = await PutAsync(host, "/mgmt/resources/cache/node-type", """{"nodeType":"cache.m7g.large"}""");

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal("cache.m7g.large", body.GetProperty("nodeType").GetString());
        Assert.Equal(ResourceEndpoints.CacheNote, body.GetProperty("note").GetString());
        Assert.Contains("dropped", ResourceEndpoints.CacheNote);
        Assert.Contains($"modify {ResourceTestHost.CacheGroup} cache.m7g.large", host.Cache.Calls);

        var row = Assert.Single(await host.AuditAsync());
        Assert.Equal(("cache", "node_type", "cache.t4g.small", "cache.m7g.large"), (row.Resource, row.Action, row.Before, row.After));
        Assert.Contains(ResourceEndpoints.CacheNote, JsonDocument.Parse(row.Detail!).RootElement.GetProperty("note").GetString());
        Assert.Single(host.ResourceEvents(), e => e.Event == "change" && Data(e).GetProperty("resource").GetString() == "cache");
    }

    // ---------- database ----------

    [Theory]
    [InlineData("""{"instanceClass":"db.m7g.large","confirm":"another-db-placeholder"}""")]
    [InlineData("""{"instanceClass":"db.m7g.large"}""")]
    public async Task DatabaseClass_400_ConfirmMismatch(string json)
    {
        await using var host = new ResourceTestHost();

        var (status, body) = await PutAsync(host, "/mgmt/resources/database/class", json);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("confirm_mismatch", body.GetProperty("error").GetString());
        Assert.DoesNotContain(host.Database.Calls, c => c.StartsWith("modify", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DatabaseClass_Upgrade_ListsEveryServiceAndTheReboot()
    {
        await using var host = new ResourceTestHost();
        await host.Factory.WithDbAsync(async db =>
        {
            db.ServiceManifests.Add(ManagementTestData.Manifest("svc-b", port: 8082));
            db.ServiceManifests.Add(ManagementTestData.Manifest("svc-a", port: 8081));
            await db.SaveChangesAsync();
        });

        var (status, body) = await PutAsync(host, "/mgmt/resources/database/class",
            """{"instanceClass":"db.m7g.large","confirm":"gateway-db-placeholder"}""");

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal(new[] { "svc-a", "svc-b" }, body.GetProperty("services").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("reboots", body.GetProperty("note").GetString());
        Assert.Contains($"modify {ResourceTestHost.DatabaseInstance} db.m7g.large", host.Database.Calls);

        var row = Assert.Single(await host.AuditAsync());
        Assert.Equal(("database", "class", "db.t4g.medium", "db.m7g.large"), (row.Resource, row.Action, row.Before, row.After));
        Assert.Single(host.ResourceEvents(), e => e.Event == "change" && Data(e).GetProperty("resource").GetString() == "database");
    }

    // db.t4g.medium documents 450 and db.t4g.small 225; the default margin is 25 %.
    [Theory]
    [InlineData("db.t4g.small", 40, 25, HttpStatusCode.Accepted)]
    [InlineData("db.t4g.small", 180, 25, HttpStatusCode.Accepted)]
    [InlineData("db.t4g.small", 181, 25, HttpStatusCode.Conflict)]
    [InlineData("db.t4g.small", 180, 50, HttpStatusCode.Conflict)]
    [InlineData("db.t4g.small", 150, 50, HttpStatusCode.Accepted)]
    [InlineData("db.t4g.micro", 100, 25, HttpStatusCode.Conflict)]
    [InlineData("db.x9.placeholder", 0, 25, HttpStatusCode.Conflict)]
    [InlineData("db.m7g.large", 400, 25, HttpStatusCode.Accepted)]
    public async Task DatabaseClass_Downgrade_NeedsTheMargin(string target, int inUse, int margin, HttpStatusCode expected)
    {
        await using var host = new ResourceTestHost(load: new LoadOptions { GeneratorGroup = ResourceTestHost.GeneratorGroup, DbDowngradeMarginPercent = margin });
        host.Database.Instances[ResourceTestHost.DatabaseInstance] =
            new DatabaseInstanceState(ResourceTestHost.DatabaseInstance, "db.t4g.medium", "available", null, inUse);

        var (status, body) = await PutAsync(host, "/mgmt/resources/database/class",
            $$"""{"instanceClass":"{{target}}","confirm":"gateway-db-placeholder"}""");

        Assert.Equal(expected, status);
        if (expected == HttpStatusCode.Conflict)
        {
            Assert.Equal("downgrade_unsafe", body.GetProperty("error").GetString());
            Assert.Equal(margin, body.GetProperty("marginPercent").GetInt32());
            Assert.DoesNotContain(host.Database.Calls, c => c.StartsWith("modify", StringComparison.Ordinal));
            Assert.Empty(await host.AuditAsync());
        }
        else
        {
            Assert.Contains($"modify {ResourceTestHost.DatabaseInstance} {target}", host.Database.Calls);
        }
    }

    [Fact]
    public async Task DatabaseClass_Downgrade_RefusedWhenConnectionsInUseAreUnknown()
    {
        await using var host = new ResourceTestHost();
        host.Database.Instances[ResourceTestHost.DatabaseInstance] =
            new DatabaseInstanceState(ResourceTestHost.DatabaseInstance, "db.t4g.medium", "available", null, null);

        var down = await PutAsync(host, "/mgmt/resources/database/class", """{"instanceClass":"db.t4g.small","confirm":"gateway-db-placeholder"}""");
        Assert.Equal(HttpStatusCode.Conflict, down.Status);

        var up = await PutAsync(host, "/mgmt/resources/database/class", """{"instanceClass":"db.m7g.large","confirm":"gateway-db-placeholder"}""");
        Assert.Equal(HttpStatusCode.Accepted, up.Status);
    }

    // ---------- audit ----------

    [Fact]
    public async Task Audit_ListsNewestFirst_WithALimit()
    {
        await using var host = new ResourceTestHost();
        await PutAsync(host, "/mgmt/resources/fleet/size", """{"size":3}""");
        await PutAsync(host, "/mgmt/resources/cache/node-type", """{"nodeType":"cache.m7g.large"}""");
        await PutAsync(host, "/mgmt/resources/database/class", """{"instanceClass":"db.m7g.large","confirm":"gateway-db-placeholder"}""");

        var all = await host.Client(ManagementApiFactory.DeployToken()).GetFromJsonAsync<JsonElement>("/mgmt/resources/audit");
        Assert.Equal(new[] { "database", "cache", "fleet" }, all.EnumerateArray().Select(r => r.GetProperty("resource").GetString()));

        var newest = all[0];
        Assert.Equal("alice-admin", newest.GetProperty("actor").GetString());
        Assert.Equal("class", newest.GetProperty("action").GetString());
        Assert.Equal("db.t4g.medium", newest.GetProperty("before").GetString());
        Assert.Equal("db.m7g.large", newest.GetProperty("after").GetString());
        Assert.Contains("reboots", newest.GetProperty("detail").GetProperty("note").GetString());
        Assert.True(newest.TryGetProperty("at", out _));

        var two = await host.Admin().GetFromJsonAsync<JsonElement>("/mgmt/resources/audit?limit=2");
        Assert.Equal(2, two.GetArrayLength());

        Assert.Equal(HttpStatusCode.BadRequest, (await host.Admin().GetAsync("/mgmt/resources/audit?limit=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Admin().GetAsync("/mgmt/resources/audit?limit=501")).StatusCode);
    }
}
