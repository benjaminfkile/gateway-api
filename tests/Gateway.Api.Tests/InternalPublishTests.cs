using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Gateway.Api.RealTime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Tests;

/// <summary>
/// Internal publish + listener-isolation tests. These need real Kestrel with two
/// distinct ports (the in-memory test server has no local-port notion), so the
/// gateway's real-time wiring is stood up on loopback exactly as Program does:
/// a public listener plus a second internal listener bound from
/// <c>GATEWAY_INTERNAL_BIND</c>.
/// </summary>
public sealed class InternalPublishTests
{
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

    private static async Task<Gateway> StartGatewayAsync()
    {
        var publicPort = GetFreePort();
        var internalPort = GetFreePort();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["urls"] = $"http://127.0.0.1:{publicPort}",
            [InternalListenerOptions.BindEnvVar] = $"127.0.0.1:{internalPort}",
        });

        builder.AddGatewayInternalListener();
        builder.Services.AddGatewayRealtime(builder.Configuration);

        var app = builder.Build();
        app.UseInternalListenerIsolation();
        app.UseWebSockets();
        app.MapGatewayHub();
        app.MapInternalPublish();

        await app.StartAsync();
        return new Gateway(app, publicPort, internalPort);
    }

    [Fact]
    public async Task InternalPublish_ReachesJoinedClient()
    {
        await using var gateway = await StartGatewayAsync();

        await using var connection = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{gateway.PublicPort}/hub")
            .Build();

        // Clients register ONE handler on the ChannelEvent method and route on the
        // envelope's channel/event fields (§4.2 wire contract).
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("ChannelEvent", envelope => received.TrySetResult(envelope));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinChannel", "svc-a:orders");

        using var http = new HttpClient();
        var response = await http.PostAsJsonAsync(
            $"http://127.0.0.1:{gateway.InternalPort}/internal/publish",
            new { channel = "svc-a:orders", @event = "orderPlaced", payload = new { id = "o-42" } });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(completed == received.Task, "joined client did not receive the published message");

        var envelope = await received.Task;
        Assert.Equal("svc-a:orders", envelope.GetProperty("channel").GetString());
        Assert.Equal("orderPlaced", envelope.GetProperty("event").GetString());
        Assert.Equal("o-42", envelope.GetProperty("data").GetProperty("id").GetString());
    }

    [Fact]
    public async Task InternalPublish_NotReachable_ViaPublicPort()
    {
        await using var gateway = await StartGatewayAsync();

        using var http = new HttpClient();
        var response = await http.PostAsJsonAsync(
            $"http://127.0.0.1:{gateway.PublicPort}/internal/publish",
            new { channel = "svc-a:orders", @event = "x", payload = new { } });

        // The public listener must never expose the internal surface (tech-spec §8).
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Hub_NotReachable_ViaInternalPort()
    {
        await using var gateway = await StartGatewayAsync();

        using var http = new HttpClient();
        // The internal listener hosts /internal/* only; /hub belongs to the public one.
        var response = await http.GetAsync(
            $"http://127.0.0.1:{gateway.InternalPort}/hub/negotiate?negotiateVersion=1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
