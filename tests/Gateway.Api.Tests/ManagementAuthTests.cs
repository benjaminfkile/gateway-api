using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Gateway.Api.Auth;
using Gateway.Api.Data;
using Gateway.Api.Manifest;
using Gateway.Api.Proxy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gateway.Api.Tests;

/// <summary>
/// Integration coverage for the management-plane authentication (tech-spec §5):
/// the <c>/mgmt/*</c> surface answers 503 when the plane is unconfigured and 401
/// unauthenticated; the OpsAdmin/OpsDeploy policies gate on Cognito groups and the
/// CI <c>mgmt/deploy</c> scope; and — the transparency regression — proxy, health,
/// and public hub channels still need no token even with auth switched on.
/// </summary>
public class ManagementAuthTests
{
    private sealed class LoopbackAddressResolver : IServiceAddressResolver
    {
        public string Resolve(ServiceManifest manifest) => $"http://127.0.0.1:{manifest.Port}";
    }

    /// <summary>Factory with the Cognito authority set → management plane configured.</summary>
    private sealed class ConfiguredAuthFactory : WebApplicationFactory<Program>
    {
        public InMemoryManifestStore Store { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [ManagementAuthOptions.AuthorityEnvVar] = TestManagementAuth.Issuer,
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IManifestStore>();
                services.AddSingleton<IManifestStore>(Store);

                services.RemoveAll<IServiceAddressResolver>();
                services.AddSingleton<IServiceAddressResolver, LoopbackAddressResolver>();

                // Trust the local test key instead of Cognito's JWKS — no network.
                services.PostConfigure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    TestManagementAuth.ApplyTestSigningKey);
            });
        }
    }

    /// <summary>Factory with no authority set → management plane unconfigured.</summary>
    private sealed class UnconfiguredAuthFactory : WebApplicationFactory<Program>
    {
    }

    private static ServiceManifest RunningManifest(string name, int port) => new()
    {
        Name = name,
        Image = $"registry/{name}",
        Tag = "latest",
        Port = port,
        DesiredStatus = "running",
        IncludeInHealth = true,
        UpdatedBy = "test",
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static HttpRequestMessage Whoami(string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/mgmt/whoami");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    // ----- 503 when unconfigured, 401 unauthenticated (AC 1546) -----

    [Fact]
    public async Task Mgmt_Unconfigured_Returns503_WithMessage()
    {
        await using var factory = new UnconfiguredAuthFactory();
        var client = factory.CreateClient();

        // Even a *valid-looking* token cannot open an unconfigured plane.
        var response = await client.SendAsync(Whoami(TestManagementAuth.CreateToken(
            groups: new[] { ManagementAuth.OpsAdminGroup })));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("not configured", await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mgmt_Configured_NoToken_Returns401()
    {
        await using var factory = new ConfiguredAuthFactory();
        var client = factory.CreateClient();

        var response = await client.SendAsync(Whoami());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Mgmt_Configured_ExpiredToken_Returns401()
    {
        await using var factory = new ConfiguredAuthFactory();
        var client = factory.CreateClient();

        var expired = TestManagementAuth.CreateToken(
            groups: new[] { ManagementAuth.OpsAdminGroup },
            expires: DateTime.UtcNow.AddMinutes(-5));

        var response = await client.SendAsync(Whoami(expired));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Mgmt_Configured_IdToken_Rejected401()
    {
        await using var factory = new ConfiguredAuthFactory();
        var client = factory.CreateClient();

        // token_use=id must be rejected — the plane accepts access tokens only.
        var idToken = TestManagementAuth.CreateToken(
            groups: new[] { ManagementAuth.OpsAdminGroup },
            tokenUse: "id");

        var response = await client.SendAsync(Whoami(idToken));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ----- OpsAdmin / OpsDeploy incl. CI scope (AC 1545) -----

    [Fact]
    public async Task Mgmt_OpsAdminToken_Authorized_IsAdminAndCanDeploy()
    {
        await using var factory = new ConfiguredAuthFactory();
        var client = factory.CreateClient();

        var response = await client.SendAsync(Whoami(TestManagementAuth.CreateToken(
            groups: new[] { ManagementAuth.OpsAdminGroup })));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("isOpsAdmin").GetBoolean());
        Assert.True(body.GetProperty("canDeploy").GetBoolean());
    }

    [Fact]
    public async Task Mgmt_OpsDeployToken_Authorized_NotAdminButCanDeploy()
    {
        await using var factory = new ConfiguredAuthFactory();
        var client = factory.CreateClient();

        var response = await client.SendAsync(Whoami(TestManagementAuth.CreateToken(
            groups: new[] { ManagementAuth.OpsDeployGroup })));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("isOpsAdmin").GetBoolean());
        Assert.True(body.GetProperty("canDeploy").GetBoolean());
    }

    [Fact]
    public async Task Mgmt_CiScopeToken_Authorized_CanDeploy()
    {
        await using var factory = new ConfiguredAuthFactory();
        var client = factory.CreateClient();

        // CI client-credentials token: no groups, scope mgmt/deploy only.
        var response = await client.SendAsync(Whoami(TestManagementAuth.CreateToken(
            scopes: new[] { ManagementAuth.DeployScope })));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("isOpsAdmin").GetBoolean());
        Assert.True(body.GetProperty("canDeploy").GetBoolean());
    }

    [Fact]
    public async Task Mgmt_AuthenticatedButNoRole_Forbidden403()
    {
        await using var factory = new ConfiguredAuthFactory();
        var client = factory.CreateClient();

        // A valid access token with neither an ops group nor the deploy scope:
        // authenticated (not 401) but not authorized for deploy (403).
        var response = await client.SendAsync(Whoami(TestManagementAuth.CreateToken()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ----- Transparency invariant regression (AC 1547) -----

    [Fact]
    public async Task Health_NeedsNoAuth_EvenWhenPlaneConfigured()
    {
        await using var factory = new ConfiguredAuthFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ProxiedRoute_NeedsNoAuth_EvenWhenPlaneConfigured()
    {
        await using var downstream = await DownstreamTestServer.StartAsync();
        await using var factory = new ConfiguredAuthFactory();
        await factory.Store.UpsertAsync(RunningManifest("svc-a", downstream.Port));

        var client = factory.CreateClient();

        // No Authorization header. Turning the ops plane on must not make the
        // gateway authenticate downstream application traffic (design invariant).
        var response = await client.GetAsync("/svc-a/secret");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/secret", await response.Content.ReadAsStringAsync());
    }
}
