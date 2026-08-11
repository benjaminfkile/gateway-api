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
/// Integration coverage for delegated (private) channel auth (task #594 — the
/// Pusher/Ably auth-delegation pattern). A service with a <c>realtime_auth_path</c> has
/// its channels gated by its own auth callback: the gateway POSTs
/// <c>{ channel, credential, connectionId }</c> to that path (reached exactly as the
/// health prober reaches the service, via <see cref="ServiceHostPortMap"/> +
/// <see cref="IServiceAddressResolver"/>) and only a <c>200 { allow:true }</c> admits
/// the join. Everything else fails closed. Uses the real SignalR client over the
/// in-memory server and the <see cref="DownstreamTestServer"/> pattern for the callback.
/// </summary>
public class DelegatedChannelAuthTests
{
    private const string Service = "svc-a";
    private const string AuthPath = "/realtime/auth";
    private const string PrivateChannel = "svc-a:private";

    /// <summary>Records each auth callback and replies with a test-configured decision.</summary>
    private sealed class AuthCallback
    {
        private readonly Func<JsonElement, HttpContext, Task> _respond;
        private int _count;

        public AuthCallback(Func<JsonElement, HttpContext, Task> respond) => _respond = respond;

        /// <summary>How many times the callback path was hit.</summary>
        public int Count => Volatile.Read(ref _count);

        /// <summary>The last request body the callback received.</summary>
        public (string Channel, string? Credential, string ConnectionId)? Last { get; private set; }

        public async Task<bool> HandleAsync(HttpContext ctx)
        {
            if (ctx.Request.Path != AuthPath)
            {
                return false;
            }

            Interlocked.Increment(ref _count);

            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            var root = doc.RootElement;
            Last = (
                root.GetProperty("channel").GetString()!,
                root.TryGetProperty("credential", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null,
                root.GetProperty("connectionId").GetString()!);

            await _respond(root, ctx);
            return true;
        }
    }

    private static Task WriteAllowAsync(HttpContext ctx, string? identity = null)
    {
        ctx.Response.ContentType = "application/json";
        var body = identity is null ? "{\"allow\":true}" : $"{{\"allow\":true,\"identity\":\"{identity}\"}}";
        return ctx.Response.WriteAsync(body);
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
                // The presence of an auth path is what makes the channel private (#594).
                manifest.RealtimeAuthPath = AuthPath;
                services.AddSingleton<IManifestStore>(new InMemoryManifestStore(new[] { manifest }));
            });
        }
    }

    /// <summary>
    /// Wire a factory whose <see cref="Service"/> resolves to <paramref name="downstream"/>
    /// via the learned-host-port map — the same mechanism the health prober uses.
    /// </summary>
    private static AuthHubFactory NewFactory(DownstreamTestServer downstream)
    {
        var factory = new AuthHubFactory(downstream.Port);
        // Touch Services to build the host, then point the host-port map at the callback
        // target so the auth client reaches it just like a real learned container port.
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
    public async Task PrivateChannel_AuthAllows_JoinsAndReceivesEnvelope()
    {
        var callback = new AuthCallback((_, ctx) => WriteAllowAsync(ctx, identity: "user-42"));
        await using var downstream = await DownstreamTestServer.StartAsync(callback.HandleAsync);
        await using var factory = NewFactory(downstream);
        await using var connection = BuildConnection(factory);

        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>(IChannelEventPublisher.ChannelEventMethod, env => received.TrySetResult(env));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinPrivateChannel", PrivateChannel, "opaque-credential");

        // The gateway forwarded exactly the opaque credential, unparsed.
        Assert.Equal(1, callback.Count);
        Assert.Equal(PrivateChannel, callback.Last!.Value.Channel);
        Assert.Equal("opaque-credential", callback.Last!.Value.Credential);
        Assert.False(string.IsNullOrEmpty(callback.Last!.Value.ConnectionId));

        // Admitted → a group broadcast reaches the client in the ChannelEvent envelope.
        var publisher = factory.Services.GetRequiredService<IChannelEventPublisher>();
        await publisher.PublishAsync(PrivateChannel, "changed", new { status = "green" });

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(completed == received.Task, "client did not receive the group broadcast");
        var envelope = await received.Task;
        Assert.Equal(PrivateChannel, envelope.GetProperty("channel").GetString());
        Assert.Equal("changed", envelope.GetProperty("event").GetString());
        Assert.Equal("green", envelope.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task PrivateChannel_AuthDenies_JoinRejected_GenericMessage()
    {
        var callback = new AuthCallback((_, ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            return ctx.Response.WriteAsync("{\"allow\":false}");
        });
        await using var downstream = await DownstreamTestServer.StartAsync(callback.HandleAsync);
        await using var factory = NewFactory(downstream);
        await using var connection = BuildConnection(factory);

        await connection.StartAsync();
        var ex = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("JoinPrivateChannel", PrivateChannel, "cred"));

        // Generic message — leaks neither that the channel exists nor why it was denied
        // (SignalR wraps the server HubException message; assert it is carried, and that
        // nothing channel-specific leaks).
        Assert.Contains(GatewayHub.AuthDeniedMessage, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateChannel, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("5xx")]
    [InlineData("garbage")]
    [InlineData("timeout")]
    public async Task PrivateChannel_CallbackMisbehaves_FailsClosed(string mode)
    {
        var callback = new AuthCallback(async (_, ctx) =>
        {
            switch (mode)
            {
                case "5xx":
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    break;
                case "garbage":
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync("this is not json");
                    break;
                case "timeout":
                    // Outlast the gateway's 2s callback timeout; end when it aborts.
                    try { await Task.Delay(TimeSpan.FromSeconds(10), ctx.RequestAborted); }
                    catch (OperationCanceledException) { }
                    break;
            }
        });
        await using var downstream = await DownstreamTestServer.StartAsync(callback.HandleAsync);
        await using var factory = NewFactory(downstream);
        await using var connection = BuildConnection(factory);

        await connection.StartAsync();
        var ex = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("JoinPrivateChannel", PrivateChannel, "cred"));
        Assert.Contains(GatewayHub.AuthDeniedMessage, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrivateChannel_CredentialedRejoin_ReAuthorizes_CredentiallessRidesCache()
    {
        // Review finding (supersedes the old second-join-cached assertion): an EXPLICIT
        // credential is the documented RENEWAL path — it must reach the owner's callback
        // and refresh the allow's TTL, or every continuously-subscribed private member
        // gets force-evicted on a 15-minute metronome with no way to prevent it. Only a
        // credential-LESS join rides the cached allow.
        var callback = new AuthCallback((_, ctx) => WriteAllowAsync(ctx));
        await using var downstream = await DownstreamTestServer.StartAsync(callback.HandleAsync);
        await using var factory = NewFactory(downstream);
        await using var connection = BuildConnection(factory);

        await connection.StartAsync();
        await connection.InvokeAsync("JoinPrivateChannel", PrivateChannel, "cred");
        await connection.InvokeAsync("JoinPrivateChannel", PrivateChannel, "cred-refreshed");
        Assert.Equal(2, callback.Count);

        // A join presenting NO credential (the group re-add path) uses the cached allow.
        await connection.InvokeAsync("JoinPrivateChannel", PrivateChannel, null);
        Assert.Equal(2, callback.Count);
    }

    [Fact]
    public async Task PrivateChannel_DenyCached_SecondJoin_DoesNotReCallback()
    {
        var callback = new AuthCallback((_, ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            return ctx.Response.WriteAsync("{\"allow\":false}");
        });
        await using var downstream = await DownstreamTestServer.StartAsync(callback.HandleAsync);
        await using var factory = NewFactory(downstream);
        await using var connection = BuildConnection(factory);

        await connection.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("JoinPrivateChannel", PrivateChannel, "cred"));
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("JoinPrivateChannel", PrivateChannel, "cred"));

        // The brief denial cache blunts a brute-force loop: the second denied join within
        // the window is served from cache, not a fresh callback.
        Assert.Equal(1, callback.Count);
    }

    [Fact]
    public async Task PrivateChannel_DenyThenDifferentValidCredential_Succeeds_AndReCallsAuth()
    {
        // Task #608 finding 1: the deny cache is keyed on the credential, so a client that
        // retries with a DIFFERENT, now-valid credential (the normal token-refresh flow) is
        // not short-circuited by the brief deny — the owning service sees the new credential.
        var callback = new AuthCallback((root, ctx) =>
        {
            var credential = root.TryGetProperty("credential", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
            return credential == "good-token"
                ? WriteAllowAsync(ctx, identity: "user-7")
                : ctx.Response.WriteAsync("{\"allow\":false}");
        });
        await using var downstream = await DownstreamTestServer.StartAsync(callback.HandleAsync);
        await using var factory = NewFactory(downstream);
        await using var connection = BuildConnection(factory);

        await connection.StartAsync();

        // First join presents a stale credential and is denied.
        await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("JoinPrivateChannel", PrivateChannel, "stale-token"));
        Assert.Equal(1, callback.Count);

        // Retrying with a fresh, valid credential re-hits the callback (the deny was keyed
        // to the stale one) and is admitted.
        await connection.InvokeAsync("JoinPrivateChannel", PrivateChannel, "good-token");
        Assert.Equal(2, callback.Count);
        Assert.Equal("good-token", callback.Last!.Value.Credential);

        // Admitted → a broadcast reaches the client.
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>(IChannelEventPublisher.ChannelEventMethod, env => received.TrySetResult(env));
        var publisher = factory.Services.GetRequiredService<IChannelEventPublisher>();
        await publisher.PublishAsync(PrivateChannel, "changed", new { ok = true });

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(completed == received.Task, "admitted client did not receive the broadcast");
    }

    [Fact]
    public async Task OneArgJoin_StillBinds_ForOpsChannel()
    {
        // SignalR optional hub-method params: the dashboard's existing one-argument
        // JoinChannel("ops:fleet") must still bind after the credential parameter was
        // added (task #594). Anonymous ops:* is rejected by the auth hook — the point is
        // that the one-arg call resolves to the method at all, throwing the ops auth
        // HubException rather than a method-binding error.
        var callback = new AuthCallback((_, ctx) => WriteAllowAsync(ctx));
        await using var downstream = await DownstreamTestServer.StartAsync(callback.HandleAsync);
        await using var factory = NewFactory(downstream);
        await using var connection = BuildConnection(factory);

        await connection.StartAsync();
        var ex = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("JoinChannel", "ops:fleet"));
        Assert.Contains("authenticated", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, callback.Count);
    }

    [Fact]
    public async Task PublicChannel_NoAuthPath_JoinUnchanged()
    {
        // A service WITHOUT an auth path keeps public channels: a join needs no callback.
        var callback = new AuthCallback((_, ctx) => WriteAllowAsync(ctx));
        await using var downstream = await DownstreamTestServer.StartAsync(callback.HandleAsync);

        var factory = new PublicHubFactory();
        await using var _ = factory;
        await using var connection = BuildConnection(factory);

        await connection.StartAsync();
        // svc-a has no realtime_auth_path here → public: join succeeds with no callback.
        await connection.InvokeAsync("JoinChannel", "svc-a:public");
        Assert.Equal(0, callback.Count);
    }

    private sealed class PublicHubFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IManifestStore>();
                // No RealtimeAuthPath → public channels (as before #594).
                services.AddSingleton<IManifestStore>(new InMemoryManifestStore(
                    new[] { ManagementTestData.Manifest(Service, includeInHealth: false) }));
            });
        }
    }
}
