using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.RealTime;

/// <summary>
/// <see cref="IChannelEventPublisher"/> over <see cref="IHubContext{GatewayHub}"/>.
/// Wraps every broadcast in the <c>{ channel, event, data }</c> envelope and sends it
/// on the single <see cref="IChannelEventPublisher.ChannelEventMethod"/> client method
/// to <c>Clients.Group(channel)</c>.
/// </summary>
public sealed class ChannelEventPublisher : IChannelEventPublisher
{
    private readonly IHubContext<GatewayHub> _hub;
    private readonly ILogger<ChannelEventPublisher> _logger;

    public ChannelEventPublisher(IHubContext<GatewayHub> hub, ILogger<ChannelEventPublisher> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task PublishAsync(string channel, string @event, object data, CancellationToken ct = default) =>
        _hub.Clients.Group(channel).SendAsync(
            IChannelEventPublisher.ChannelEventMethod,
            new { channel, @event, data },
            ct);

    /// <inheritdoc />
    public void TryPublish(string channel, string @event, object data)
    {
        // Best-effort: swallow EVERYTHING (backplane outage, serialization, a synchronous
        // throw from a faulted hub) so a broadcast can never fail an already-committed
        // caller. The publish still runs asynchronously; failures are logged, not thrown.
        _ = PublishSwallowingAsync(channel, @event, data);
    }

    private async Task PublishSwallowingAsync(string channel, string @event, object data)
    {
        try
        {
            await PublishAsync(channel, @event, data);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish '{Event}' on channel '{Channel}'; continuing (broadcast is best-effort).",
                @event,
                channel);
        }
    }
}
