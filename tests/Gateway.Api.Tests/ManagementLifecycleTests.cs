using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gateway.Api.Data;
using Gateway.Api.Management;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Tests;

/// <summary>
/// Integration coverage for the lifecycle endpoints (tech-spec §4.5):
/// stop/start/restart update desired status, the force-flag guardrail on a
/// health-check dependency, the reserved <c>gateway</c> name, the authz matrix
/// (OpsAdmin required), and the audit trail written to <c>deploy_history</c>.
/// </summary>
public class ManagementLifecycleTests
{
    private static async Task SeedAsync(ManagementApiFactory factory, ServiceManifest manifest)
    {
        await factory.WithDbAsync(async db =>
        {
            db.ServiceManifests.Add(manifest);
            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Start_SetsDesiredRunning_AndAudits()
    {
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("svc-a", desiredStatus: "stopped", includeInHealth: false));

        var client = factory.CreateClient(ManagementApiFactory.AdminToken("alice"));
        var response = await client.PostAsync("/mgmt/services/svc-a/start", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory.WithDbAsync(async db =>
        {
            var m = await db.ServiceManifests.SingleAsync(x => x.Name == "svc-a");
            Assert.Equal("running", m.DesiredStatus);
            Assert.Equal("alice", m.UpdatedBy);

            var audit = await db.DeployHistory.SingleAsync();
            Assert.Equal(DeployAction.Start, audit.Action);
            Assert.Equal("alice", audit.Actor);
            Assert.Equal(DeployStatus.Done, audit.Status);
        });
    }

    [Fact]
    public async Task Stop_HealthDependency_WithoutForce_Returns409()
    {
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("svc-a", includeInHealth: true));

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.PostAsync("/mgmt/services/svc-a/stop", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // The guardrail must not have changed desired status, nor audited.
        await factory.WithDbAsync(async db =>
        {
            var m = await db.ServiceManifests.SingleAsync(x => x.Name == "svc-a");
            Assert.Equal("running", m.DesiredStatus);
            Assert.Equal(0, await db.DeployHistory.CountAsync());
        });
    }

    [Fact]
    public async Task Stop_HealthDependency_WithForce_Succeeds()
    {
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("svc-a", includeInHealth: true));

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.PostAsync("/mgmt/services/svc-a/stop?force=true", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory.WithDbAsync(async db =>
        {
            var m = await db.ServiceManifests.SingleAsync(x => x.Name == "svc-a");
            Assert.Equal("stopped", m.DesiredStatus);
        });
    }

    [Fact]
    public async Task Stop_NonHealthService_NeedsNoForce()
    {
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("svc-a", includeInHealth: false));

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.PostAsync("/mgmt/services/svc-a/stop", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Restart_StampsRestartRequestedAt_AndAudits()
    {
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("svc-a", includeInHealth: false));
        var before = DateTimeOffset.UtcNow;

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.PostAsync("/mgmt/services/svc-a/restart", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory.WithDbAsync(async db =>
        {
            var m = await db.ServiceManifests.SingleAsync(x => x.Name == "svc-a");
            // updated_at was bumped off the seeded UnixEpoch.
            Assert.True(m.UpdatedAt > DateTimeOffset.UnixEpoch);
            // The restart request is stamped to ~now so the reconciler recreates any
            // container older than it (tech-spec §4.5).
            Assert.NotNull(m.RestartRequestedAt);
            Assert.True(m.RestartRequestedAt >= before);
            // A running service stays running; the digest/tag are untouched.
            Assert.Equal("running", m.DesiredStatus);
            var audit = await db.DeployHistory.SingleAsync();
            Assert.Equal(DeployAction.Restart, audit.Action);
        });
    }

    [Fact]
    public async Task Restart_StoppedService_StartsIt_AndStampsRequest()
    {
        // Requirement #4: restart on a stopped service behaves like start — it sets
        // desired=running — and also stamps restart_requested_at.
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("svc-a", desiredStatus: "stopped", includeInHealth: false));

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.PostAsync("/mgmt/services/svc-a/restart", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory.WithDbAsync(async db =>
        {
            var m = await db.ServiceManifests.SingleAsync(x => x.Name == "svc-a");
            Assert.Equal("running", m.DesiredStatus);
            Assert.NotNull(m.RestartRequestedAt);
        });
    }

    [Fact]
    public async Task Stop_UnknownService_Returns404()
    {
        await using var factory = new ManagementApiFactory();

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.PostAsync("/mgmt/services/ghost/stop", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("start")]
    [InlineData("restart")]
    public async Task GatewayName_IsRejected_400(string action)
    {
        await using var factory = new ManagementApiFactory();

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.PostAsync($"/mgmt/services/gateway/{action}", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Stop_PreExistingReservedRow_Succeeds()
    {
        // FINDING 2b: reservation blocks CREATION, not teardown. A row named "hub" was
        // legal before the reservation and keeps generating a shadowing YARP route — it
        // must remain stoppable via the API so an operator can retire it without editing
        // the DB by hand.
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("hub", includeInHealth: false));

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.PostAsync("/mgmt/services/hub/stop", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory.WithDbAsync(async db =>
        {
            var m = await db.ServiceManifests.SingleAsync(x => x.Name == "hub");
            Assert.Equal("stopped", m.DesiredStatus);
        });
    }

    [Fact]
    public async Task Start_PreExistingReservedRow_StillRejected()
    {
        // FINDING 2b: start is NOT a teardown path — it re-asserts running and keeps the
        // shadowing route alive — so it stays blocked even when a reserved-named row exists.
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("hub", desiredStatus: "stopped", includeInHealth: false));

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.PostAsync("/mgmt/services/hub/start", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Lifecycle_DeployToken_Forbidden_NotAdmin()
    {
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("svc-a", includeInHealth: false));

        // ops-deploy is not ops-admin: stop/start/restart are admin-only.
        var client = factory.CreateClient(ManagementApiFactory.DeployToken());
        var response = await client.PostAsync("/mgmt/services/svc-a/start", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Lifecycle_NoToken_Unauthorized()
    {
        await using var factory = new ManagementApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync("/mgmt/services/svc-a/start", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
