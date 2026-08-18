using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gateway.Api.Data;

namespace Gateway.Api.Tests;

/// <summary>
/// Integration coverage for the fleet-aware read endpoints (tech-spec §4.5):
/// <c>GET /mgmt/services</c> (manifest + rollup computed from <c>instance_status</c>)
/// and <c>GET /mgmt/instances</c> (fleet list with a &gt;90s staleness flag). The
/// rollup math and staleness are asserted against seeded Sqlite rows.
/// </summary>
public class ManagementServicesReadTests
{
    [Fact]
    public async Task GetServices_ComputesFleetRollup_FromInstanceStatus()
    {
        await using var factory = new ManagementApiFactory();
        await factory.WithDbAsync(async db =>
        {
            db.ServiceManifests.Add(ManagementTestData.Manifest("svc-a", digest: "sha256:v2"));
            // Three live instances: two on v2, one still on v1.
            db.InstanceStatus.Add(ManagementTestData.Instance("i-1", isLeader: true, running: ("svc-a", "sha256:v2")));
            db.InstanceStatus.Add(ManagementTestData.Instance("i-2", running: ("svc-a", "sha256:v2")));
            db.InstanceStatus.Add(ManagementTestData.Instance("i-3", running: ("svc-a", "sha256:v1")));
            await db.SaveChangesAsync();
        });

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var body = await client.GetFromJsonAsync<JsonElement>("/mgmt/services");

        var svc = body.EnumerateArray().Single();
        Assert.Equal("svc-a", svc.GetProperty("name").GetString());
        var fleet = svc.GetProperty("fleet");
        Assert.Equal(3, fleet.GetProperty("runningOn").GetInt32());
        Assert.Equal(3, fleet.GetProperty("totalInstances").GetInt32());

        var digests = fleet.GetProperty("digests");
        Assert.Equal(2, digests.GetProperty("sha256:v2").GetInt32());
        Assert.Equal(1, digests.GetProperty("sha256:v1").GetInt32());
    }

    [Fact]
    public async Task GetServices_RunningOn_ExcludesInstancesNotRunningService()
    {
        await using var factory = new ManagementApiFactory();
        await factory.WithDbAsync(async db =>
        {
            db.ServiceManifests.Add(ManagementTestData.Manifest("svc-a", digest: "sha256:v1"));
            db.InstanceStatus.Add(ManagementTestData.Instance("i-1", running: ("svc-a", "sha256:v1")));
            // i-2 runs a different service only — must not count toward svc-a.
            db.InstanceStatus.Add(ManagementTestData.Instance("i-2", running: ("svc-b", "sha256:x")));
            await db.SaveChangesAsync();
        });

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var body = await client.GetFromJsonAsync<JsonElement>("/mgmt/services");

        var svcA = body.EnumerateArray().Single(s => s.GetProperty("name").GetString() == "svc-a");
        var fleet = svcA.GetProperty("fleet");
        Assert.Equal(1, fleet.GetProperty("runningOn").GetInt32());
        Assert.Equal(2, fleet.GetProperty("totalInstances").GetInt32());
    }

    [Fact]
    public async Task GetInstances_FlagsStaleRows_OlderThan90s()
    {
        await using var factory = new ManagementApiFactory();
        await factory.WithDbAsync(async db =>
        {
            db.InstanceStatus.Add(ManagementTestData.Instance(
                "i-fresh", isLeader: true, heartbeatAt: DateTimeOffset.UtcNow, running: ("svc-a", "sha256:v1")));
            db.InstanceStatus.Add(ManagementTestData.Instance(
                "i-stale", heartbeatAt: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5)));
            await db.SaveChangesAsync();
        });

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var body = await client.GetFromJsonAsync<JsonElement>("/mgmt/instances");

        var rows = body.EnumerateArray().ToDictionary(r => r.GetProperty("instanceId").GetString()!);
        Assert.False(rows["i-fresh"].GetProperty("stale").GetBoolean());
        Assert.True(rows["i-stale"].GetProperty("stale").GetBoolean());
        Assert.True(rows["i-fresh"].GetProperty("isLeader").GetBoolean());

        // The per-instance service inventory is surfaced parsed.
        var svc = rows["i-fresh"].GetProperty("services").EnumerateArray().Single();
        Assert.Equal("svc-a", svc.GetProperty("name").GetString());
        Assert.Equal("sha256:v1", svc.GetProperty("digest").GetString());
    }

    [Fact]
    public async Task GetServices_RollsUpErrors_CountAndLatest()
    {
        // task #553: the fleet rollup exposes errorOn (instances whose entry carries a
        // lastError) and latestError (the most recent one). A service failing to start
        // on every instance is visible even with runningOn == 0.
        var older = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2);
        var newer = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10);

        await using var factory = new ManagementApiFactory();
        await factory.WithDbAsync(async db =>
        {
            db.ServiceManifests.Add(ManagementTestData.Manifest("svc-a", digest: "sha256:v2"));
            // Two instances failing to start svc-a (absent-but-desired), one healthy.
            db.InstanceStatus.Add(ManagementTestData.InstanceWithError(
                "i-1", "svc-a", "No such image (older)", older));
            db.InstanceStatus.Add(ManagementTestData.InstanceWithError(
                "i-2", "svc-a", "No such image (newest)", newer));
            db.InstanceStatus.Add(ManagementTestData.Instance("i-3", running: ("svc-a", "sha256:v2")));
            await db.SaveChangesAsync();
        });

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var body = await client.GetFromJsonAsync<JsonElement>("/mgmt/services");

        var svc = body.EnumerateArray().Single(s => s.GetProperty("name").GetString() == "svc-a");
        var fleet = svc.GetProperty("fleet");
        Assert.Equal(1, fleet.GetProperty("runningOn").GetInt32());
        Assert.Equal(3, fleet.GetProperty("totalInstances").GetInt32());
        Assert.Equal(2, fleet.GetProperty("errorOn").GetInt32());

        var latest = fleet.GetProperty("latestError");
        Assert.Equal("i-2", latest.GetProperty("instanceId").GetString());
        Assert.Equal("No such image (newest)", latest.GetProperty("message").GetString());
    }

    [Fact]
    public async Task GetServices_NoErrors_LatestErrorIsNull()
    {
        await using var factory = new ManagementApiFactory();
        await factory.WithDbAsync(async db =>
        {
            db.ServiceManifests.Add(ManagementTestData.Manifest("svc-a", digest: "sha256:v1"));
            db.InstanceStatus.Add(ManagementTestData.Instance("i-1", running: ("svc-a", "sha256:v1")));
            await db.SaveChangesAsync();
        });

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var body = await client.GetFromJsonAsync<JsonElement>("/mgmt/services");

        var fleet = body.EnumerateArray().Single().GetProperty("fleet");
        Assert.Equal(0, fleet.GetProperty("errorOn").GetInt32());
        Assert.Equal(JsonValueKind.Null, fleet.GetProperty("latestError").ValueKind);
    }

    [Fact]
    public async Task GetInstances_PassesThrough_LastErrorPerServiceEntry()
    {
        // task #553: GET /mgmt/instances surfaces lastError/lastErrorAt on each service
        // entry — including an absent-but-desired service that keeps failing to start.
        var at = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30);

        await using var factory = new ManagementApiFactory();
        await factory.WithDbAsync(async db =>
        {
            db.InstanceStatus.Add(ManagementTestData.InstanceWithError(
                "i-1", "svc-a", "boom: No such image", at, heartbeatAt: DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        });

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var body = await client.GetFromJsonAsync<JsonElement>("/mgmt/instances");

        var row = body.EnumerateArray().Single(r => r.GetProperty("instanceId").GetString() == "i-1");
        var svc = row.GetProperty("services").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "svc-a");
        Assert.Equal("absent", svc.GetProperty("state").GetString());
        Assert.Equal("boom: No such image", svc.GetProperty("lastError").GetString());
        Assert.Equal(JsonValueKind.String, svc.GetProperty("lastErrorAt").ValueKind);
    }

    [Fact]
    public async Task GetServices_RollsUpDegradedInstances_FromDigestDriftBreaker()
    {
        // task #99: the fleet rollup exposes degradedOn — the count of instances
        // whose reconciler has tripped the digest-drift circuit breaker for a
        // service (pinned digest unsatisfiable). Distinct from errorOn: the
        // containers are still running (last successfully started), so runningOn
        // is non-zero even when degradedOn is the fleet size.
        await using var factory = new ManagementApiFactory();
        await factory.WithDbAsync(async db =>
        {
            db.ServiceManifests.Add(ManagementTestData.Manifest("svc-a", digest: "sha256:pinned"));
            // Two instances degraded (breaker tripped), one healthy.
            db.InstanceStatus.Add(ManagementTestData.InstanceWithDegradedService("i-1", "svc-a"));
            db.InstanceStatus.Add(ManagementTestData.InstanceWithDegradedService("i-2", "svc-a"));
            db.InstanceStatus.Add(ManagementTestData.Instance("i-3", running: ("svc-a", "sha256:pinned")));
            await db.SaveChangesAsync();
        });

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var body = await client.GetFromJsonAsync<JsonElement>("/mgmt/services");

        var svc = body.EnumerateArray().Single(s => s.GetProperty("name").GetString() == "svc-a");
        var fleet = svc.GetProperty("fleet");
        Assert.Equal(3, fleet.GetProperty("runningOn").GetInt32()); // all still serving
        Assert.Equal(2, fleet.GetProperty("degradedOn").GetInt32());
    }

    [Fact]
    public async Task GetServices_NoDegraded_ReportsZero()
    {
        await using var factory = new ManagementApiFactory();
        await factory.WithDbAsync(async db =>
        {
            db.ServiceManifests.Add(ManagementTestData.Manifest("svc-a", digest: "sha256:v1"));
            db.InstanceStatus.Add(ManagementTestData.Instance("i-1", running: ("svc-a", "sha256:v1")));
            await db.SaveChangesAsync();
        });

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var body = await client.GetFromJsonAsync<JsonElement>("/mgmt/services");

        var fleet = body.EnumerateArray().Single().GetProperty("fleet");
        Assert.Equal(0, fleet.GetProperty("degradedOn").GetInt32());
    }

    [Fact]
    public async Task GetServices_RequiresAuthentication()
    {
        await using var factory = new ManagementApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/mgmt/services");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetServices_CiToken_AllowedRead()
    {
        await using var factory = new ManagementApiFactory();
        var client = factory.CreateClient(ManagementApiFactory.CiToken());

        var response = await client.GetAsync("/mgmt/services");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetServices_NoRoleToken_Forbidden()
    {
        await using var factory = new ManagementApiFactory();
        var client = factory.CreateClient(ManagementApiFactory.NoRoleToken());

        var response = await client.GetAsync("/mgmt/services");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
