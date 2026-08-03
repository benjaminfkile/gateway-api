using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Tests;

/// <summary>
/// A real Kestrel HTTP/WebSocket server standing in for a downstream service
/// container. It is gateway-unaware: it echoes back the exact path + query it
/// received (so tests can prove the gateway strips the '/{name}' prefix and
/// preserves the remainder) and echoes WebSocket frames (to prove passthrough).
/// </summary>
public sealed class DownstreamTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private DownstreamTestServer(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    /// <summary>The dynamically-assigned localhost port the server is listening on.</summary>
    public int Port { get; }

    public static async Task<DownstreamTestServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.UseWebSockets();

        app.Map("/{**catch-all}", async (HttpContext ctx) =>
        {
            if (ctx.WebSockets.IsWebSocketRequest)
            {
                using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
                await EchoLoopAsync(socket, ctx.RequestAborted);
                return;
            }

            // Echo the path + query exactly as received by the downstream app.
            var pathAndQuery = ctx.Request.Path.Value + ctx.Request.QueryString.Value;
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.WriteAsync(pathAndQuery);
        });

        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
        var port = new Uri(address).Port;

        return new DownstreamTestServer(app, port);
    }

    private static async Task EchoLoopAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                return;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var reply = Encoding.UTF8.GetBytes("echo:" + text);
            await socket.SendAsync(new ArraySegment<byte>(reply), WebSocketMessageType.Text, true, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
