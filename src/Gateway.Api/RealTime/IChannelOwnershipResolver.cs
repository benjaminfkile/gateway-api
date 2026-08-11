namespace Gateway.Api.RealTime;

/// <summary>
/// Resolves the owner of a real-time channel prefix (tech-spec §4.2, task #593).
/// A channel is <c>{prefix}:{topic}</c>; every non-<c>ops</c> prefix must be the name
/// of an existing manifest service, and only that service may publish to it. This is
/// the single place the hub (on <c>JoinChannel</c>) and the internal publish endpoint
/// consult ownership, so both share one short-lived cache and never hammer the DB.
/// </summary>
public interface IChannelOwnershipResolver
{
    /// <summary>
    /// The manifest service that owns <paramref name="prefix"/>, or null when no
    /// manifest service is named <paramref name="prefix"/>. The <c>ops</c> prefix is
    /// gateway-owned and is never resolved here — callers handle it before asking.
    /// </summary>
    Task<ChannelOwner?> ResolveAsync(string prefix, CancellationToken ct = default);
}

/// <summary>
/// The owner of a channel prefix: the service <paramref name="Service"/>, its current
/// publish token (null on a pre-migration row that has no token yet), the optional
/// delegated-auth path (task #594 — null means the service's channels are public), the
/// optional message path (task #611 — null means the full-duplex <c>SendToChannel</c>
/// feature is off for the service), and the manifest (container-internal) port used as
/// the address-resolution fallback when no host port has been learned yet.
/// </summary>
public sealed record ChannelOwner(
    string Service, string? PublishToken, string? AuthPath, string? MessagePath, int Port);
