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

    /// <summary>
    /// The single choke point for turning a presented <c>X-Gateway-Realtime-Token</c>
    /// value into a manifest service (task #217). Constant-time-compares
    /// <paramref name="presentedToken"/> against <b>every</b> known service's stored
    /// token — even after finding a match — so both the comparison time and the number
    /// of comparisons are independent of which service (or none) owns the token.
    /// Returns the matching <see cref="ChannelOwner"/>, or null when the token is
    /// missing, empty, or does not match any known service. Every internal owner-scoped
    /// endpoint (<c>/internal/publish</c>, <c>/internal/presence</c>,
    /// <c>/internal/leader</c>) resolves the presenter through this one method so the
    /// header-read and compare live in exactly one place.
    /// </summary>
    Task<ChannelOwner?> ResolveByTokenAsync(string? presentedToken, CancellationToken ct = default);
}

/// <summary>
/// The owner of a channel prefix: the service <paramref name="Service"/>, its current
/// publish token (null on a pre-migration row that has no token yet), the optional
/// delegated-auth path (task #594 — null means the service's channels are public), the
/// optional message path (task #611 — null means the full-duplex <c>SendToChannel</c>
/// feature is off for the service), whether the service opted in to presence events
/// (task #612 — <c>false</c> by default; gates the coalesced <c>presence</c> broadcast,
/// not the owner presence API), and the manifest (container-internal) port used as the
/// address-resolution fallback when no host port has been learned yet.
/// </summary>
public sealed record ChannelOwner(
    string Service, string? PublishToken, string? AuthPath, string? MessagePath, bool PresenceEnabled, int Port);
