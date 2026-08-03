using Gateway.Api.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Api.Tests;

/// <summary>
/// Hub-side coverage of the ops:* channel gate once real Cognito auth is wired
/// (tech-spec §4.2, §5). With the plane configured, a human ops token may join
/// <c>ops:*</c>; a valid token without an ops group may not; public channels stay
/// open to anyone (transparency invariant). Long-polling carries the token in the
/// Authorization header, so no query-string handling is needed for the test.
/// </summary>
public class ManagementHubAuthTests
{
    private sealed class ConfiguredHubFactory : WebApplicationFactory<Program>
    {
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
                services.PostConfigure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    TestManagementAuth.ApplyTestSigningKey);
            });
        }
    }

    private static HubConnection BuildConnection(
        WebApplicationFactory<Program> factory, string? token = null)
    {
        var server = factory.Server;
        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hub"), HttpTransportType.LongPolling, options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                if (token is not null)
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                }
            })
            .Build();
    }

    [Fact]
    public async Task OpsChannel_OpsAdminToken_MayJoin()
    {
        await using var factory = new ConfiguredHubFactory();
        var token = TestManagementAuth.CreateToken(groups: new[] { ManagementAuth.OpsAdminGroup });
        await using var connection = BuildConnection(factory, token);

        await connection.StartAsync();

        // No exception → the ops:* authorization policy accepted the ops principal.
        await connection.InvokeAsync("JoinChannel", "ops:fleet");
    }

    [Fact]
    public async Task OpsChannel_AuthenticatedButNoOpsGroup_IsRejected()
    {
        await using var factory = new ConfiguredHubFactory();
        // A valid access token that is not in an ops group (e.g. the CI deploy
        // token) must not reach the dashboard channels.
        var token = TestManagementAuth.CreateToken(scopes: new[] { ManagementAuth.DeployScope });
        await using var connection = BuildConnection(factory, token);

        await connection.StartAsync();

        var ex = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("JoinChannel", "ops:fleet"));
        Assert.Contains("authenticated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublicChannel_NeedsNoToken_EvenWhenPlaneConfigured()
    {
        await using var factory = new ConfiguredHubFactory();
        await using var connection = BuildConnection(factory);

        await connection.StartAsync();

        // A non-ops channel stays public broadcast regardless of the ops plane.
        await connection.InvokeAsync("JoinChannel", "svc-a:public");
    }
}
