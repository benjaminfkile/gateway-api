using System.Net;
using Gateway.Api.Data;
using Gateway.Api.Management;
using Gateway.Api.Proxy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Configuration;

namespace Gateway.Api.Tests;

/// <summary>
/// Integration coverage for <c>DELETE /mgmt/services/{name}</c> (tech-spec §4.5):
/// the authz matrix (OpsAdmin required), 404 for an unknown service, the
/// include_in_health force-flag guardrail (mirroring stop), that the manifest row
/// is actually removed and the proxy route dropped, and the audit trail written
/// to <c>deploy_history</c>.
/// </summary>
public class ManagementDeleteTests
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
    public async Task Delete_NonHealthService_RemovesRow_AndAudits()
    {
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("svc-a", includeInHealth: false, digest: "sha256:v1"));

        var client = factory.CreateClient(ManagementApiFactory.AdminToken("alice"));
        var response = await client.DeleteAsync("/mgmt/services/svc-a");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory.WithDbAsync(async db =>
        {
            Assert.False(await db.ServiceManifests.AnyAsync(x => x.Name == "svc-a"));

            var audit = await db.DeployHistory.SingleAsync();
            Assert.Equal(DeployAction.Delete, audit.Action);
            Assert.Equal("alice", audit.Actor);
            Assert.Equal(DeployStatus.Done, audit.Status);
            // from = the digest that was live; to = null on removal.
            Assert.Equal("sha256:v1", audit.FromDigest);
            Assert.Null(audit.ToDigest);
        });
    }

    [Fact]
    public async Task Delete_HealthDependency_WithoutForce_Returns409()
    {
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("svc-a", includeInHealth: true));

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.DeleteAsync("/mgmt/services/svc-a");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // The guardrail must not have removed the row, nor audited.
        await factory.WithDbAsync(async db =>
        {
            Assert.True(await db.ServiceManifests.AnyAsync(x => x.Name == "svc-a"));
            Assert.Equal(0, await db.DeployHistory.CountAsync());
        });
    }

    [Fact]
    public async Task Delete_HealthDependency_WithForce_RemovesRow()
    {
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("svc-a", includeInHealth: true));

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.DeleteAsync("/mgmt/services/svc-a?force=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory.WithDbAsync(async db =>
        {
            Assert.False(await db.ServiceManifests.AnyAsync(x => x.Name == "svc-a"));
            var audit = await db.DeployHistory.SingleAsync();
            Assert.Equal(DeployAction.Delete, audit.Action);
        });
    }

    [Fact]
    public async Task Delete_DropsProxyRoute_Immediately()
    {
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("svc-a", includeInHealth: false));

        // Build the route table for the seeded service (the reconciler is off in
        // the test host, so refresh explicitly to establish the pre-delete route).
        var provider = factory.Services.GetRequiredService<IProxyConfigProvider>();
        await factory.Services.GetRequiredService<ProxyStateService>().RefreshRoutesAsync();
        Assert.Contains(provider.GetConfig().Routes, r => r.RouteId == "route-svc-a");

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.DeleteAsync("/mgmt/services/svc-a");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The delete handler refreshed routes: svc-a's route is gone without a
        // reconcile loop, so traffic to it drops on this serving instance.
        Assert.DoesNotContain(provider.GetConfig().Routes, r => r.RouteId == "route-svc-a");
    }

    [Fact]
    public async Task Delete_UnknownService_Returns404()
    {
        await using var factory = new ManagementApiFactory();

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.DeleteAsync("/mgmt/services/ghost");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_GatewayName_IsRejected_400()
    {
        await using var factory = new ManagementApiFactory();

        var client = factory.CreateClient(ManagementApiFactory.AdminToken());
        var response = await client.DeleteAsync("/mgmt/services/gateway");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delete_DeployToken_Forbidden_NotAdmin()
    {
        await using var factory = new ManagementApiFactory();
        await SeedAsync(factory, ManagementTestData.Manifest("svc-a", includeInHealth: false));

        // ops-deploy is not ops-admin: manifest edits are admin-only.
        var client = factory.CreateClient(ManagementApiFactory.DeployToken());
        var response = await client.DeleteAsync("/mgmt/services/svc-a");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // Forbidden must not have removed the row.
        await factory.WithDbAsync(async db =>
            Assert.True(await db.ServiceManifests.AnyAsync(x => x.Name == "svc-a")));
    }

    [Fact]
    public async Task Delete_NoToken_Unauthorized()
    {
        await using var factory = new ManagementApiFactory();
        var client = factory.CreateClient();

        var response = await client.DeleteAsync("/mgmt/services/svc-a");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
