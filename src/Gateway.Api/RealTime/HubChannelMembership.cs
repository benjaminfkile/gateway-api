using System.Collections.Concurrent;

namespace Gateway.Api.RealTime;

/// <summary>
/// Tracks which channels each hub connection is currently a member of, and the identity
/// (if any) established for that membership (task #611). SignalR maps a
/// <c>Groups.AddToGroupAsync</c> onto its backplane but exposes no way to ask "is this
/// connection in that group?", so <c>SendToChannel</c> — which must reject a send to a
/// channel the caller never joined (rule a) — needs this side-registry to answer the
/// question locally.
/// <para>
/// The recorded identity is the opaque string the owning service returned from its
/// delegated-auth callback (task #594): null for public and <c>ops:*</c> channels. It
/// rides along on the forwarded message (<c>SendToChannel</c> → owner's message path) so
/// the owner knows <i>who</i> sent it without the gateway understanding the credential.
/// </para>
/// <para>
/// Singleton and thread-safe (the hub is per-invocation). Membership is inherently
/// instance-local — a connection lives on exactly one gateway instance — so no backplane
/// is involved. A whole connection's memberships drop in one call on disconnect
/// (<see cref="Drop"/>, from <c>GatewayHub.OnDisconnectedAsync</c>); a channel-scoped
/// <c>LeaveChannel</c> removes just that entry (<see cref="Leave"/>).
/// </para>
/// </summary>
public sealed class HubChannelMembership
{
    // connectionId -> (channel -> identity-or-null). Nested so a whole connection's
    // memberships drop in one operation on disconnect. A sentinel is used because a
    // ConcurrentDictionary value cannot be null; TryGetIdentity unwraps it.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string?>> _byConnection =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Record that <paramref name="connectionId"/> joined <paramref name="channel"/>,
    /// carrying <paramref name="identity"/> (null for public/ops channels). Idempotent:
    /// a re-join overwrites the stored identity with the latest decision.
    /// </summary>
    public void Join(string connectionId, string channel, string? identity)
    {
        var channels = _byConnection.GetOrAdd(
            connectionId, _ => new ConcurrentDictionary<string, string?>(StringComparer.Ordinal));
        channels[channel] = identity;
    }

    /// <summary>Remove a single channel membership (called from <c>LeaveChannel</c>).</summary>
    public void Leave(string connectionId, string channel)
    {
        if (_byConnection.TryGetValue(connectionId, out var channels))
        {
            channels.TryRemove(channel, out _);
        }
    }

    /// <summary>
    /// True when <paramref name="connectionId"/> is currently a member of
    /// <paramref name="channel"/>; on a hit <paramref name="identity"/> is the identity
    /// recorded at join time (may be null). A miss leaves it null.
    /// </summary>
    public bool TryGetIdentity(string connectionId, string channel, out string? identity)
    {
        if (_byConnection.TryGetValue(connectionId, out var channels)
            && channels.TryGetValue(channel, out identity))
        {
            return true;
        }

        identity = null;
        return false;
    }

    /// <summary>Forget every membership for a connection (called on disconnect).</summary>
    public void Drop(string connectionId) => _byConnection.TryRemove(connectionId, out _);
}
