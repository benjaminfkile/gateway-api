namespace Gateway.Api.RealTime;

/// <summary>
/// Delegates a single channel-join authorization to the owning downstream service
/// (task #594 — the Pusher/Ably auth-delegation pattern). The gateway never understands
/// the credential; it hands the service <c>{ channel, credential, connectionId }</c> and
/// trusts the service's yes/no. Every failure mode — non-200, timeout, unreachable,
/// malformed body — is a <b>deny</b> (fail closed).
/// </summary>
public interface IChannelAuthClient
{
    /// <summary>
    /// Ask <paramref name="owner"/>'s service whether <paramref name="connectionId"/>,
    /// presenting <paramref name="credential"/>, may join <paramref name="channel"/>.
    /// Never throws: any error resolves to <see cref="ChannelAuthDecision.Deny"/>.
    /// </summary>
    Task<ChannelAuthDecision> AuthorizeAsync(
        ChannelOwner owner,
        string channel,
        string? credential,
        string connectionId,
        CancellationToken ct = default);
}
