using Gateway.Api.Manifest;
using Gateway.Api.Proxy;
using Gateway.Api.RealTime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gateway.Api.Tests;

/// <summary>
/// Hub-level presence wiring (task #612): a join adds a row to the
/// <see cref="IPresenceRegistry"/>, a leave removes it, a disconnect sweeps every channel,
/// and the identity established by a delegated-auth callback flows onto the presence row.
/// Uses the real SignalR client over the in-memory server (no Redis) and reads the registry
/// straight off the host's service provider.
/// </summary>
public class PresenceHubTests
{
    private const string Service = "svc-a";
    private const string AuthPath = "/realtime/auth";

    private sealed class PublicHubFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IManifestStore>();
                services.AddSingleton<IManifestStore>(new InMemoryManifestStore(
                    new[] { ManagementTestData.Manifest(Service, includeInHealth: false) }));
            });
        }
    }

    private sealed class AuthHubFactory : WebApplicationFactory<Program>
    {
        private readonly int _port;

        public AuthHubFactory(int downstreamPort) => _port = downstreamPort;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IManifestStore>();
                var manifest = ManagementTestData.Manifest(Service, port: _port, includeInHealth: false);
                manifest.RealtimeAuthPath = AuthPath;
                services.AddSingleton<IManifestStore>(new InMemoryManifestStore(new[] { manifest }));
            });
        }
    }

    private static HubConnection BuildConnection(WebApplicationFactory<Program> factory)
    {
        var server = factory.Server;
        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hub"), HttpTransportType.LongPolling, options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
            })
            .Build();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail(because);
    }

    [Fact]
    public async Task JoinChannel_AddsPresenceRow_ForConnection()
    {
        await using var factory = new PublicHubFactory();
        await using var connection = BuildConnection(factory);
        var registry = factory.Services.GetRequiredService<IPresenceRegistry>();

        await connection.StartAsync();
        await connection.InvokeAsync("JoinChannel", "svc-a:room");

        var members = await registry.ListAsync("svc-a:room");
        var entry = Assert.Single(members);
        Assert.Equal(connection.ConnectionId, entry.ConnectionId);
        Assert.Null(entry.Identity); // public channel → no identity
    }

    [Fact]
    public async Task LeaveChannel_RemovesPresenceRow()
    {
        await using var factory = new PublicHubFactory();
        await using var connection = BuildConnection(factory);
        var registry = factory.Services.GetRequiredService<IPresenceRegistry>();

        await connection.StartAsync();
        await connection.InvokeAsync("JoinChannel", "svc-a:room");
        Assert.Equal(1, await registry.CountAsync("svc-a:room"));

        await connection.InvokeAsync("LeaveChannel", "svc-a:room");
        Assert.Equal(0, await registry.CountAsync("svc-a:room"));
    }

    [Fact]
    public async Task Disconnect_SweepsPresence_AcrossChannels()
    {
        await using var factory = new PublicHubFactory();
        var connection = BuildConnection(factory);
        var registry = factory.Services.GetRequiredService<IPresenceRegistry>();

        await connection.StartAsync();
        await connection.InvokeAsync("JoinChannel", "svc-a:one");
        await connection.InvokeAsync("JoinChannel", "svc-a:two");
        Assert.Equal(1, await registry.CountAsync("svc-a:one"));
        Assert.Equal(1, await registry.CountAsync("svc-a:two"));

        await connection.DisposeAsync();

        // OnDisconnectedAsync runs server-side asynchronously after the client drops.
        await WaitUntilAsync(
            async () => await registry.CountAsync("svc-a:one") == 0 && await registry.CountAsync("svc-a:two") == 0,
            "disconnect did not sweep presence rows");
    }

    [Fact]
    public async Task PrivateChannel_IdentityFromAuthDecision_FlowsToPresenceRow()
    {
        // The auth callback admits the join and returns identity "user-42"; that identity is
        // the one recorded on the presence row (task #612 — provenance is the auth callback).
        var callback = new AuthCallbackServer(identity: "user-42");
        await using var downstream = await DownstreamTestServer.StartAsync(callback.HandleAsync);
        await using var factory = new AuthHubFactory(downstream.Port);
        factory.Services.GetRequiredService<ServiceHostPortMap>().Set(Service, downstream.Port);
        await using var connection = BuildConnection(factory);
        var registry = factory.Services.GetRequiredService<IPresenceRegistry>();

        await connection.StartAsync();
        await connection.InvokeAsync("JoinPrivateChannel", "svc-a:private", "opaque-credential");

        var entry = Assert.Single(await registry.ListAsync("svc-a:private"));
        Assert.Equal(connection.ConnectionId, entry.ConnectionId);
        Assert.Equal("user-42", entry.Identity);
    }

    /// <summary>A minimal <c>realtime_auth_path</c> stand-in that admits with an identity.</summary>
    private sealed class AuthCallbackServer
    {
        private readonly string _identity;

        public AuthCallbackServer(string identity) => _identity = identity;

        public async Task<bool> HandleAsync(HttpContext ctx)
        {
            if (ctx.Request.Path != AuthPath)
            {
                return false;
            }

            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync($"{{\"allow\":true,\"identity\":\"{_identity}\"}}");
            return true;
        }
    }
}
