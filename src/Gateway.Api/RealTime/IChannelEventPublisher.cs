namespace Gateway.Api.RealTime;

/// <summary>
/// Publishes real-time events to dashboard/app subscribers on the shared
/// <see cref="GatewayHub"/> (tech-spec §4.2). The gateway sends exactly ONE SignalR
/// client method — <see cref="ChannelEventMethod"/> — whose single argument is the
/// envelope <c>{ channel, event, data }</c>. Clients join the SignalR group named by
/// <c>channel</c> and filter on the envelope's <c>channel</c> field. Keeping every
/// broadcast on one method name means a client registers a single handler and routes
/// on the envelope, rather than a handler per event type.
/// </summary>
public interface IChannelEventPublisher
{
    /// <summary>The single SignalR client method every gateway broadcast rides on.</summary>
    public const string ChannelEventMethod = "ChannelEvent";

    /// <summary>
    /// Send the <c>{ channel, event, data }</c> envelope to the SignalR group named
    /// by <paramref name="channel"/>. Awaited — the caller observes a backplane
    /// failure. Use on paths that can tolerate (or must surface) a publish error.
    /// </summary>
    Task PublishAsync(string channel, string @event, object data, CancellationToken ct = default);

    /// <summary>
    /// Fire-and-forget variant that catches and logs ALL exceptions. A Redis
    /// backplane outage must never fail the caller — this is for request paths where
    /// the state change has already committed and the broadcast is best-effort
    /// (tech-spec §4.5: a Redis blip must not turn a committed deploy into a 500).
    /// </summary>
    void TryPublish(string channel, string @event, object data);
}
