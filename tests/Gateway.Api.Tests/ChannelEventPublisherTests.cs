using System.Text.Json;
using Gateway.Api.RealTime;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Api.Tests;

/// <summary>
/// Unit coverage for <see cref="ChannelEventPublisher"/> (tech-spec §4.2 wire
/// contract): the gateway publishes exactly ONE client method, <c>ChannelEvent</c>,
/// whose single argument is the <c>{ channel, event, data }</c> envelope. Uses the
/// hand-rolled <see cref="FakeGatewayHubContext"/> (no live SignalR, no Redis).
/// </summary>
public class ChannelEventPublisherTests
{
    private static ChannelEventPublisher Create(FakeGatewayHubContext hub) =>
        new(hub, NullLogger<ChannelEventPublisher>.Instance);

    private static JsonElement Envelope(object? arg) =>
        JsonSerializer.SerializeToElement(arg);

    [Fact]
    public void ChannelEventMethod_IsTheContractName()
    {
        Assert.Equal("ChannelEvent", IChannelEventPublisher.ChannelEventMethod);
    }

    [Fact]
    public async Task PublishAsync_SendsChannelEventEnvelope_ToTheChannelGroup()
    {
        var hub = new FakeGatewayHubContext();
        var publisher = Create(hub);

        await publisher.PublishAsync("ops:deploys", "deploy", new { deployId = 7, service = "svc-a" });

        var (group, method, arg) = Assert.Single(hub.Sends);
        // Fanned out to the SignalR group named by the channel, on the single method.
        Assert.Equal("ops:deploys", group);
        Assert.Equal("ChannelEvent", method);

        // The one argument is the envelope with all three fields.
        var envelope = Envelope(arg);
        Assert.Equal("ops:deploys", envelope.GetProperty("channel").GetString());
        Assert.Equal("deploy", envelope.GetProperty("event").GetString());
        var data = envelope.GetProperty("data");
        Assert.Equal(7, data.GetProperty("deployId").GetInt32());
        Assert.Equal("svc-a", data.GetProperty("service").GetString());
    }

    [Fact]
    public async Task TryPublish_SendsTheSameEnvelope_OnTheHappyPath()
    {
        var hub = new FakeGatewayHubContext();
        var publisher = Create(hub);

        publisher.TryPublish("ops:fleet", "heartbeat", new { instanceCount = 3 });

        // TryPublish is fire-and-forget; give the swallowing task a moment to run.
        await Task.Delay(50);

        var (group, method, arg) = Assert.Single(hub.Sends);
        Assert.Equal("ops:fleet", group);
        Assert.Equal("ChannelEvent", method);
        var envelope = Envelope(arg);
        Assert.Equal("ops:fleet", envelope.GetProperty("channel").GetString());
        Assert.Equal("heartbeat", envelope.GetProperty("event").GetString());
        Assert.Equal(3, envelope.GetProperty("data").GetProperty("instanceCount").GetInt32());
    }

    [Fact]
    public async Task TryPublish_SwallowsAThrowingHub_AndNeverFaultsTheCaller()
    {
        var hub = new FakeGatewayHubContext
        {
            // Simulate a Redis backplane outage: every send throws.
            SendError = new InvalidOperationException("backplane down"),
        };
        var publisher = Create(hub);

        // Must not throw — a broadcast failure can never bubble to the caller.
        publisher.TryPublish("ops:deploys", "deploy", new { deployId = 1 });

        await Task.Delay(50);

        // The throw was swallowed; nothing was recorded as delivered.
        Assert.Empty(hub.Sends);
    }

    [Fact]
    public async Task PublishAsync_Propagates_WhenTheHubThrows()
    {
        var hub = new FakeGatewayHubContext
        {
            SendError = new InvalidOperationException("backplane down"),
        };
        var publisher = Create(hub);

        // The awaited variant surfaces the failure — callers that opt into it observe it.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => publisher.PublishAsync("ops:deploys", "deploy", new { deployId = 1 }));
    }
}
