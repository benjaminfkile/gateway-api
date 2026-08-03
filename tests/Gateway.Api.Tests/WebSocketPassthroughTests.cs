using System.Net.WebSockets;
using System.Text;
using Gateway.Api.Data;
using Gateway.Api.Manifest;
using Gateway.Api.Proxy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Tests;

/// <summary>
/// WebSocket passthrough is native to YARP; these tests run the gateway on a
/// real Kestrel listener (WebSockets can't be exercised through the in-memory
/// test server) and assert that an Upgrade request proxies straight through to a
/// downstream WebSocket endpoint — nothing in the pipeline blocks it.
/// </summary>
public class WebSocketPassthroughTests
{
    private sealed class LoopbackAddressResolver : IServiceAddressResolver
    {
        public string Resolve(ServiceManifest manifest) => $"http://127.0.0.1:{manifest.Port}";
    }

    private static async Task<(WebApplication app, int port)> StartGatewayAsync(IManifestStore store)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton(store);
        builder.Services.TryAddSingleton<IServiceAddressResolver, LoopbackAddressResolver>();
        builder.Services.AddManifestProxy();

        var app = builder.Build();
        app.UseWebSockets();
        app.MapReverseProxy();

        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
        return (app, new Uri(address).Port);
    }

    [Fact]
    public async Task WebSocket_ProxiesThrough_Untouched()
    {
        await using var downstream = await DownstreamTestServer.StartAsync();

        var store = new InMemoryManifestStore();
        await store.UpsertAsync(new ServiceManifest
        {
            Name = "svc-a",
            Image = "registry/svc-a",
            Tag = "latest",
            Port = downstream.Port,
            DesiredStatus = "running",
            IncludeInHealth = true,
            UpdatedBy = "test",
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });

        var (app, port) = await StartGatewayAsync(store);
        await using var _ = app;

        using var wsClient = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await wsClient.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/svc-a/live"), cts.Token);

        var payload = Encoding.UTF8.GetBytes("hello");
        await wsClient.SendAsync(payload, WebSocketMessageType.Text, true, cts.Token);

        var buffer = new byte[256];
        var result = await wsClient.ReceiveAsync(buffer, cts.Token);
        var reply = Encoding.UTF8.GetString(buffer, 0, result.Count);

        Assert.Equal("echo:hello", reply);

        await wsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
    }
}
