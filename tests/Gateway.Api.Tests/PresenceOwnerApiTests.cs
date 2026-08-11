using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Gateway.Api.Data;
using Gateway.Api.Manifest;
using Gateway.Api.RealTime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Tests;

/// <summary>
/// Owner presence API tests (task #612): <c>GET /internal/presence/{channel}</c> is guarded
/// by the SAME owner-token check as <c>/internal/publish</c> and returns who is present plus
/// a count. Needs real Kestrel with two ports (the in-memory server has no local-port notion),
/// stood up exactly as <see cref="InternalPublishTests"/> does.
/// </summary>
public sealed class PresenceOwnerApiTests
{
    private const string TokenA = "token-a-secret";
    private const string TokenB = "token-b-secret";

    private sealed record Gateway(WebApplication App, int PublicPort, int InternalPort) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static ServiceManifest Owned(string name, string token) => new()
    {
        Name = name,
        Image = $"registry/{name}",
        Tag = "latest",
        Port = 8080,
        DesiredStatus = "running",
        RealtimePublishToken = token,
        UpdatedBy = "seed",
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static async Task<Gateway> StartGatewayAsync()
    {
        var publicPort = GetFreePort();
        var internalPort = GetFreePort();

        var config = new Dictionary<string, string?>
        {
            ["urls"] = $"http://127.0.0.1:{publicPort}",
            [InternalListenerOptions.BindEnvVar] = $"127.0.0.1:{internalPort}",
        };

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(config);

        builder.AddGatewayInternalListener();
        builder.Services.AddGatewayRealtime(builder.Configuration);
        builder.Services.AddSingleton<IManifestStore>(new InMemoryManifestStore(new[]
        {
            Owned("svc-a", TokenA),
            Owned("svc-b", TokenB),
        }));

        var app = builder.Build();
        app.UseInternalListenerIsolation();
        app.UseWebSockets();
        app.MapGatewayHub();
        app.MapInternalPublish();
        app.MapInternalPresence();

        await app.StartAsync();
        return new Gateway(app, publicPort, internalPort);
    }

    private static async Task<HttpResponseMessage> GetPresenceAsync(Gateway gateway, string channel, string? token)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"http://127.0.0.1:{gateway.InternalPort}/internal/presence/{channel}");
        if (token is not null)
        {
            request.Headers.Add(RealtimePublishToken.Header, token);
        }

        return await http.SendAsync(request);
    }

    [Fact]
    public async Task Presence_WithOwnerToken_ReturnsJoinedConnection()
    {
        await using var gateway = await StartGatewayAsync();

        await using var connection = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{gateway.PublicPort}/hub")
            .Build();
        await connection.StartAsync();
        await connection.InvokeAsync("JoinChannel", "svc-a:room");

        var response = await GetPresenceAsync(gateway, "svc-a:room", TokenA);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("svc-a:room", root.GetProperty("channel").GetString());
        Assert.Equal(1, root.GetProperty("count").GetInt32());
        var member = Assert.Single(root.GetProperty("members").EnumerateArray());
        Assert.Equal(connection.ConnectionId, member.GetProperty("connectionId").GetString());
    }

    [Fact]
    public async Task Presence_MissingToken_IsForbidden()
    {
        await using var gateway = await StartGatewayAsync();
        var response = await GetPresenceAsync(gateway, "svc-a:room", token: null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Presence_WrongToken_IsForbidden()
    {
        await using var gateway = await StartGatewayAsync();
        // svc-b's token does not authorize reading svc-a's presence.
        var response = await GetPresenceAsync(gateway, "svc-a:room", TokenB);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Presence_UnknownPrefix_IsForbidden()
    {
        await using var gateway = await StartGatewayAsync();
        var response = await GetPresenceAsync(gateway, "ghost:room", TokenA);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Presence_OpsChannel_IsForbidden_EvenWithToken()
    {
        await using var gateway = await StartGatewayAsync();
        var response = await GetPresenceAsync(gateway, "ops:fleet", TokenA);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Presence_MalformedChannel_IsBadRequest()
    {
        await using var gateway = await StartGatewayAsync();
        // "svc-a" has no topic segment → the shared {service}:{topic} shape check rejects it.
        var response = await GetPresenceAsync(gateway, "svc-a", TokenA);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Presence_NotReachable_ViaPublicPort()
    {
        await using var gateway = await StartGatewayAsync();
        using var http = new HttpClient();
        var response = await http.GetAsync(
            $"http://127.0.0.1:{gateway.PublicPort}/internal/presence/svc-a:room");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
