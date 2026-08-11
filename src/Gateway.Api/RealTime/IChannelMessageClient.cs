namespace Gateway.Api.RealTime;

/// <summary>
/// Forwards a client-originated <c>SendToChannel</c> message to the owning downstream
/// service's <c>realtime_message_path</c> (task #611 — the full-duplex companion to
/// <see cref="IChannelAuthClient"/>). The gateway never interprets the payload and never
/// broadcasts it; it POSTs <c>{ channel, event, data, connectionId, identity }</c> to the
/// owner and reports only whether the owner accepted delivery.
/// </summary>
public interface IChannelMessageClient
{
    /// <summary>
    /// POST the message to <paramref name="owner"/>'s message path. Returns <c>true</c>
    /// when the owner answered <c>2xx</c> (the response body is ignored — fire-and-forget
    /// toward the client), and <c>false</c> on any non-2xx, timeout, or transport failure
    /// (which the hub surfaces to the sending client as a thrown error). Never throws.
    /// </summary>
    Task<bool> ForwardAsync(
        ChannelOwner owner,
        string channel,
        string @event,
        object? data,
        string connectionId,
        string? identity,
        CancellationToken ct = default);
}
