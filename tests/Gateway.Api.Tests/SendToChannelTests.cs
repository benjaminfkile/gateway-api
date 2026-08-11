using System.Text.Json;
using Gateway.Api.Manifest;
using Gateway.Api.Proxy;
using Gateway.Api.RealTime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gateway.Api.Tests;

/// <summary>
/// Integration coverage for full-duplex client→owner messaging (task #611). A service with
/// a <c>realtime_message_path</c> receives messages its clients send via
/// <c>SendToChannel</c>: the gateway POSTs <c>{ channel, event, data, connectionId, identity }</c>
/// to that path (reached exactly as the auth callback reaches the service, via
/// <see cref="ServiceHostPortMap"/> + <see cref="IServiceAddressResolver"/>). The gateway
/// never broadcasts the message itself. Enforced in order: membership, opt-in config, rate
/// limit; a non-2xx/timeout on the forward is surfaced to the sender.
/// </summary>
public class SendToChannelTests
{
    private const string Service = "svc-a";
    private const string AuthPath = "/realtime/auth";
    private const string MessagePath = "/realtime/message";

    /// <summary>Records forwarded messages and replies with a test-configured status.</summary>
    private sealed class DownstreamRecorder
    {
        private readonly Func<HttpContext, Task>? _messageRespond;
        private int _messageCount;

        public DownstreamRecorder(Func<HttpContext, Task>? messageRespond = null) =>
            _messageRespond = messageRespond;

        public int MessageCount => Volatile.Read(ref _messageCount);

        public (string Channel, string Event, string? Identity, string ConnectionId, JsonElement Data)? LastMessage { get; private set; }

        public async Task<bool> HandleAsync(HttpContext ctx)
        {
            if (ctx.Request.Path == AuthPath)
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                ctx.Response.ContentType = "application/json";
                // Admit and hand back an identity so the forward can be asserted to carry it.
                await ctx.Response.WriteAsync("{\"allow\":true,\"identity\":\"user-42\"}");
                return true;
            }

            if (ctx.Request.Path == MessagePath)
            {
                Interlocked.Increment(ref _messageCount);
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                var root = doc.RootElement;
                LastMessage = (
                    root.GetProperty("channel").GetString()!,
                    root.GetProperty("event").GetString()!,
                    root.TryGetProperty("identity", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null,
                    root.GetProperty("connectionId").GetString()!,
                    root.GetProperty("data").Clone());

                if (_messageRespond is not null)
                {
                    await _messageRespond(ctx);
                }
                else
                {
                    ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                }

                return true;
            }

            return false;
        }
    }

    private sealed class MsgHubFactory : WebApplicationFactory<Program>
    {
        private readonly int _port;
        private readonly string? _authPath;
        private readonly string? _messagePath;

        public MsgHubFactory(int downstreamPort, string? authPath, string? messagePath)
        {
            _port = downstreamPort;
            _authPath = authPath;
            _messagePath = messagePath;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IManifestStore>();
                var manifest = ManagementTestData.Manifest(Service, port: _port, includeInHealth: false);
                manifest.RealtimeAuthPath = _authPath;
                manifest.RealtimeMessagePath = _messagePath;
                services.AddSingleton<IManifestStore>(new InMemoryManifestStore(new[] { manifest }));
            });
        }
    }

    private static MsgHubFactory NewFactory(DownstreamTestServer downstream, string? authPath, string? messagePath)
    {
        var factory = new MsgHubFactory(downstream.Port, authPath, messagePath);
        factory.Services.GetRequiredService<ServiceHostPortMap>().Set(Service, downstream.Port);
        return factory;
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

    [Fact]
    public async Task SendToChannel_Forward_CarriesIdentityAndFields()
    {
        var recorder = new DownstreamRecorder();
        await using var downstream = await DownstreamTestServer.StartAsync(recorder.HandleAsync);
        await using var factory = NewFactory(downstream, AuthPath, MessagePath);
        await using var connection = BuildConnection(factory);

        await connection.StartAsync();
        // Join a private channel so the auth callback establishes the identity that must ride
        // along on the forward.
        await connection.InvokeAsync("JoinPrivateChannel", "svc-a:room", "cred");

        await connection.InvokeAsync("SendToChannel", "svc-a:room", "typed", new { text = "hello" });

        Assert.Equal(1, recorder.MessageCount);
        var last = recorder.LastMessage!.Value;
        Assert.Equal("svc-a:room", last.Channel);
        Assert.Equal("typed", last.Event);
        Assert.Equal("user-42", last.Identity);
        Assert.False(string.IsNullOrEmpty(last.ConnectionId));
        Assert.Equal("hello", last.Data.GetProperty("text").GetString());
    }

    [Fact]
    public async Task SendToChannel_PublicChannel_ForwardsNullIdentity()
    {
        var recorder = new DownstreamRecorder();
        await using var downstream = await DownstreamTestServer.StartAsync(recorder.HandleAsync);
        // No auth path → public channel → identity is null on the forward.
        await using var factory = NewFactory(downstream, authPath: null, messagePath: MessagePath);
        await using var connection = BuildConnection(factory);

        await connection.StartAsync();
        await connection.InvokeAsync("JoinChannel", "svc-a:public");
        await connection.InvokeAsync("SendToChannel", "svc-a:public", "evt", new { n = 1 });

        Assert.Equal(1, recorder.MessageCount);
        Assert.Null(recorder.LastMessage!.Value.Identity);
    }

    [Fact]
    public async Task SendToChannel_NotAMember_RejectedGeneric()
    {
        var recorder = new DownstreamRecorder();
        await using var downstream = await DownstreamTestServer.StartAsync(recorder.HandleAsync);
        await using var factory = NewFactory(downstream, authPath: null, messagePath: MessagePath);
        await using var connection = BuildConnection(factory);

        await connection.StartAsync();
        // No JoinChannel first → not a member → generic denial, no forward.
        var ex = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("SendToChannel", "svc-a:public", "evt", new { n = 1 }));
        Assert.Contains(GatewayHub.AuthDeniedMessage, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, recorder.MessageCount);
    }

    [Fact]
    public async Task SendToChannel_NoMessagePathConfigured_Rejected_Distinct()
    {
        var recorder = new DownstreamRecorder();
        await using var downstream = await DownstreamTestServer.StartAsync(recorder.HandleAsync);
        // Service is joinable (public) but has NO message path → feature off.
        await using var factory = NewFactory(downstream, authPath: null, messagePath: null);
        await using var connection = BuildConnection(factory);

        await connection.StartAsync();
        await connection.InvokeAsync("JoinChannel", "svc-a:public");

        var ex = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("SendToChannel", "svc-a:public", "evt", new { n = 1 }));
        // Distinct clear error, not the generic denial.
        Assert.Contains(GatewayHub.MessagingNotEnabledMessage, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, recorder.MessageCount);
    }

    [Fact]
    public async Task SendToChannel_OwnerReturns5xx_SurfacesToCaller()
    {
        var recorder = new DownstreamRecorder(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        });
        await using var downstream = await DownstreamTestServer.StartAsync(recorder.HandleAsync);
        await using var factory = NewFactory(downstream, authPath: null, messagePath: MessagePath);
        await using var connection = BuildConnection(factory);

        await connection.StartAsync();
        await connection.InvokeAsync("JoinChannel", "svc-a:public");

        var ex = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("SendToChannel", "svc-a:public", "evt", new { n = 1 }));
        Assert.Contains(GatewayHub.DeliveryFailedMessage, ex.Message, StringComparison.Ordinal);
        // The owner WAS reached (it chose to 500); the failure is surfaced, not swallowed.
        Assert.Equal(1, recorder.MessageCount);
    }

    [Fact]
    public async Task SendToChannel_RateLimit_TripsAndRecovers()
    {
        var recorder = new DownstreamRecorder();
        await using var downstream = await DownstreamTestServer.StartAsync(recorder.HandleAsync);

        // Substitute a fake-clock message rate limiter with a tiny burst so the trip and the
        // recovery are deterministic without a wall-clock sleep.
        var clock = new TestClock();
        var factory = new RateLimitedHubFactory(downstream.Port, clock);
        await using var _ = factory;
        factory.Services.GetRequiredService<ServiceHostPortMap>().Set(Service, downstream.Port);
        await using var connection = BuildConnection(factory);

        await connection.StartAsync();
        await connection.InvokeAsync("JoinChannel", "svc-a:public");

        // Burst is 2: two sends succeed, the third trips the limit.
        await connection.InvokeAsync("SendToChannel", "svc-a:public", "e", new { i = 1 });
        await connection.InvokeAsync("SendToChannel", "svc-a:public", "e", new { i = 2 });
        var ex = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("SendToChannel", "svc-a:public", "e", new { i = 3 }));
        Assert.Contains(GatewayHub.MessageRateLimitedMessage, ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, recorder.MessageCount);

        // Advance the clock past one refill (rate 10/s → 0.1s per token) and it recovers.
        clock.Now += TimeSpan.FromSeconds(1);
        await connection.InvokeAsync("SendToChannel", "svc-a:public", "e", new { i = 4 });
        Assert.Equal(3, recorder.MessageCount);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RateLimitedHubFactory : WebApplicationFactory<Program>
    {
        private readonly int _port;
        private readonly TestClock _clock;

        public RateLimitedHubFactory(int downstreamPort, TestClock clock)
        {
            _port = downstreamPort;
            _clock = clock;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IManifestStore>();
                var manifest = ManagementTestData.Manifest(Service, port: _port, includeInHealth: false);
                manifest.RealtimeMessagePath = MessagePath;
                services.AddSingleton<IManifestStore>(new InMemoryManifestStore(new[] { manifest }));

                // Fake-clock limiter with burst 2 so the trip/recover is deterministic.
                services.RemoveAll<MessageRateLimiter>();
                services.AddSingleton(new MessageRateLimiter(
                    new RealtimeRateLimitOptions { MessageRate = 10, MessageBurst = 2 }, _clock));
            });
        }
    }
}
