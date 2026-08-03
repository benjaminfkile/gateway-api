using System.Text.Json;
using Gateway.Api.RealTime;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Api.Tests;

/// <summary>
/// Hub integration tests over <see cref="WebApplicationFactory{TEntryPoint}"/> and
/// the real SignalR client — no Redis. The client rides the in-memory test server
/// via long-polling (the transport that needs only the test handler), which is
/// enough to prove channel groups and the ops:* authorization hook end-to-end.
/// </summary>
public class HubIntegrationTests
{
    private sealed class HubTestFactory : WebApplicationFactory<Program>
    {
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
    public async Task JoinChannel_ThenGroupBroadcast_ReachesClient()
    {
        await using var factory = new HubTestFactory();
        await using var connection = BuildConnection(factory);

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("updated", payload =>
            received.TrySetResult(payload.GetProperty("status").GetString()!));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinChannel", "svc-a:updates");

        // Broadcast to the group via the hub context — the round trip that proves
        // JoinChannel really subscribed the connection to the SignalR group.
        var hub = factory.Services.GetRequiredService<IHubContext<GatewayHub>>();
        await hub.Clients.Group("svc-a:updates").SendAsync("updated", new { status = "green" });

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(completed == received.Task, "client did not receive the group broadcast");
        Assert.Equal("green", await received.Task);
    }

    [Fact]
    public async Task LeaveChannel_StopsFurtherBroadcasts()
    {
        await using var factory = new HubTestFactory();
        await using var connection = BuildConnection(factory);

        var hits = 0;
        connection.On<JsonElement>("tick", _ => Interlocked.Increment(ref hits));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinChannel", "svc-a:ticks");
        await connection.InvokeAsync("LeaveChannel", "svc-a:ticks");

        var hub = factory.Services.GetRequiredService<IHubContext<GatewayHub>>();
        await hub.Clients.Group("svc-a:ticks").SendAsync("tick", new { });

        // Give any (unexpected) delivery a chance to arrive before asserting none did.
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(0, hits);
    }

    [Fact]
    public async Task JoinOpsChannel_Anonymous_IsRejected()
    {
        await using var factory = new HubTestFactory();
        await using var connection = BuildConnection(factory);

        await connection.StartAsync();

        // No authentication is wired yet; the ops:* authorization hook must reject
        // the anonymous join (tech-spec §4.2) — the connection itself stays up.
        var ex = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("JoinChannel", "ops:fleet"));
        Assert.Contains("authenticated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task JoinPublicChannel_Anonymous_IsAllowed()
    {
        await using var factory = new HubTestFactory();
        await using var connection = BuildConnection(factory);

        await connection.StartAsync();

        // A non-ops channel is public broadcast — no auth required (design invariant).
        await connection.InvokeAsync("JoinChannel", "svc-a:public");
    }
}
