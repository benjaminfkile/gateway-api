using System.Net;
using System.Net.Http.Json;
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

    // Two owned channels (task #593): svc-a and svc-b are manifest services, each with
    // its own publish token. A downstream may publish only to its own prefix.
    private const string TokenA = "token-a-secret";
    private const string TokenB = "token-b-secret";

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

    private static async Task<Gateway> StartGatewayAsync(
        IDictionary<string, string?>? extraConfig = null)
    {
        var publicPort = GetFreePort();
        var internalPort = GetFreePort();

        var config = new Dictionary<string, string?>
        {
            ["urls"] = $"http://127.0.0.1:{publicPort}",
            [InternalListenerOptions.BindEnvVar] = $"127.0.0.1:{internalPort}",
        };
        if (extraConfig is not null)
        {
            foreach (var (k, v) in extraConfig)
            {
                config[k] = v;
            }
        }

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(config);

        builder.AddGatewayInternalListener();
        builder.Services.AddGatewayRealtime(builder.Configuration);

        // The publish endpoint and the hub's JoinChannel resolve channel ownership
        // through the manifest store (task #593); seed the owned services + tokens.
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

        await app.StartAsync();
        return new Gateway(app, publicPort, internalPort);
    }

    /// <summary>POST /internal/publish with an optional publish token header.</summary>
    private static async Task<HttpResponseMessage> PublishAsync(
        Gateway gateway, string channel, object payload, string? token)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"http://127.0.0.1:{gateway.InternalPort}/internal/publish")
        {
            Content = JsonContent.Create(new { channel, @event = "evt", payload }),
        };
        if (token is not null)
        {
            request.Headers.Add(RealtimePublishToken.Header, token);
        }

        return await http.SendAsync(request);
    }

    [Fact]
    public async Task JoinChannel_SendsImmediateJoinedAck()
    {
        // A fresh subscriber must get its first provable delivery IMMEDIATELY (the
        // caller-only "joined" ack), not whenever the next broadcast fires — for the
        // dashboard's ops:fleet that gap was up to a full ~30s heartbeat interval of
        // grey dot on an entirely healthy connection.
        await using var gateway = await StartGatewayAsync();

        await using var connection = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{gateway.PublicPort}/hub")
            .Build();

        var acked = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("ChannelEvent", envelope =>
        {
            if (envelope.GetProperty("event").GetString() == GatewayHub.JoinedAckEvent)
            {
                acked.TrySetResult(envelope);
            }
        });

        await connection.StartAsync();
        await connection.InvokeAsync("JoinChannel", "svc-a:orders");

        var completed = await Task.WhenAny(acked.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(completed == acked.Task, "join did not produce an immediate joined ack");

        var ack = await acked.Task;
        Assert.Equal("svc-a:orders", ack.GetProperty("channel").GetString());
        Assert.Equal("svc-a:orders", ack.GetProperty("data").GetProperty("channel").GetString());
    }

    [Fact]
    public async Task InternalPublish_ReachesJoinedClient()
    {
        await using var gateway = await StartGatewayAsync();

        await using var connection = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{gateway.PublicPort}/hub")
            .Build();

        // Clients register ONE handler on the ChannelEvent method and route on the
        // envelope's channel/event fields (§4.2 wire contract). Joins now emit an
        // immediate caller-only "joined" ack (instant liveness), so route past it to
        // the broadcast this test is about.
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("ChannelEvent", envelope =>
        {
            if (envelope.GetProperty("event").GetString() != GatewayHub.JoinedAckEvent)
            {
                received.TrySetResult(envelope);
            }
        });

        await connection.StartAsync();
        await connection.InvokeAsync("JoinChannel", "svc-a:orders");

        using var http = new HttpClient();
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"http://127.0.0.1:{gateway.InternalPort}/internal/publish")
        {
            Content = JsonContent.Create(
                new { channel = "svc-a:orders", @event = "orderPlaced", payload = new { id = "o-42" } }),
        };
        // svc-a owns svc-a:*, so its token authorizes the publish (task #593).
        request.Headers.Add(RealtimePublishToken.Header, TokenA);
        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(completed == received.Task, "joined client did not receive the published message");

        var envelope = await received.Task;
        Assert.Equal("svc-a:orders", envelope.GetProperty("channel").GetString());
        Assert.Equal("orderPlaced", envelope.GetProperty("event").GetString());
        Assert.Equal("o-42", envelope.GetProperty("data").GetProperty("id").GetString());
    }

    [Fact]
    public async Task InternalPublish_WrongToken_IsForbidden()
    {
        await using var gateway = await StartGatewayAsync();

        // svc-b's token does not authorize svc-a's channel (cross-service token).
        var response = await PublishAsync(gateway, "svc-a:orders", new { id = "x" }, TokenB);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task InternalPublish_MissingToken_IsForbidden()
    {
        await using var gateway = await StartGatewayAsync();

        var response = await PublishAsync(gateway, "svc-a:orders", new { id = "x" }, token: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task InternalPublish_UnknownPrefix_IsForbidden()
    {
        await using var gateway = await StartGatewayAsync();

        // No manifest service named 'ghost' → no owner → 403 regardless of any token.
        var response = await PublishAsync(gateway, "ghost:orders", new { id = "x" }, TokenA);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("svc-a")]   // no separator at all
    [InlineData("svc-a:")]  // present prefix, empty topic
    public async Task InternalPublish_MalformedChannel_IsBadRequest(string channel)
    {
        await using var gateway = await StartGatewayAsync();

        // Task #608 finding 3: a channel that can never be joined (no {service}:{topic}
        // shape) must be rejected 400 before ownership/token checks — otherwise it would
        // 202 and broadcast into a permanently-empty group (silent event loss). svc-a IS a
        // known service with a valid token, isolating the shape check from ownership/auth.
        var response = await PublishAsync(gateway, channel, new { id = "x" }, TokenA);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task InternalPublish_OpsChannel_IsForbidden_EvenWithToken()
    {
        await using var gateway = await StartGatewayAsync();

        // ops:* is gateway-owned and never publishable through the HTTP passthrough,
        // regardless of any token — gateway-internal events bypass this endpoint.
        var response = await PublishAsync(gateway, "ops:fleet", new { id = "x" }, TokenA);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task InternalPublish_CorrectToken_FansOutEnvelope_Succeeds()
    {
        await using var gateway = await StartGatewayAsync();

        var response = await PublishAsync(gateway, "svc-b:events", new { id = "b-1" }, TokenB);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task InternalPublish_OverBudget_429_WithRetryAfter()
    {
        // Task #611 item 4: a per-service publish throttle. Configure a tiny burst so a rapid
        // run of authorized publishes trips it, returning 429 with a Retry-After header.
        await using var gateway = await StartGatewayAsync(new Dictionary<string, string?>
        {
            [RealtimeRateLimitOptions.PublishRateEnvVar] = "1",
            [RealtimeRateLimitOptions.PublishBurstEnvVar] = "3",
        });

        // The burst of 3 succeeds; the 4th within the same instant is over budget.
        for (var i = 0; i < 3; i++)
        {
            var ok = await PublishAsync(gateway, "svc-a:orders", new { i }, TokenA);
            Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        }

        var throttled = await PublishAsync(gateway, "svc-a:orders", new { i = 3 }, TokenA);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.True(throttled.Headers.TryGetValues("Retry-After", out var values));
        Assert.False(string.IsNullOrWhiteSpace(values!.First()));

        // The throttle is per service: svc-b has its own untouched budget.
        var other = await PublishAsync(gateway, "svc-b:events", new { i = 0 }, TokenB);
        Assert.Equal(HttpStatusCode.Accepted, other.StatusCode);
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
