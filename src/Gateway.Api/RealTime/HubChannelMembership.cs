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
/// This registry — not the best-effort presence view — is the AUTHORITATIVE record of
/// this instance's memberships (review finding): the security-critical eviction sweep
/// enumerates it via <see cref="Snapshot"/>, so a failed presence write can never exempt
/// a connection from eviction.
/// </para>
/// <para>
/// Singleton and thread-safe (the hub is per-invocation). Membership is inherently
/// instance-local — a connection lives on exactly one gateway instance — so no backplane
/// is involved. A whole connection's memberships drop in one call on disconnect
/// (<see cref="Drop"/>, from <c>GatewayHub.OnDisconnectedAsync</c>); a channel-scoped
/// <c>LeaveChannel</c> removes just that entry (<see cref="Leave"/>). Dropped connection
/// ids are tombstoned briefly so a join suspended across the disconnect (awaiting the
/// group add or the ~2s auth callback) cannot resurrect a membership for a dead
/// connection (review finding — the same race the decision cache tombstones).
/// </para>
/// </summary>
public sealed class HubChannelMembership
{
    /// <summary>How long a dropped connection id refuses late joins.</summary>
    public static readonly TimeSpan DefaultTombstoneTtl = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _clock;
    private readonly TimeSpan _tombstoneTtl;

    // connectionId -> (channel -> identity-or-null). Nested so a whole connection's
    // memberships drop in one operation on disconnect.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string?>> _byConnection =
        new(StringComparer.Ordinal);

    // Recently-dropped connection ids -> tombstone expiry.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _tombstones = new(StringComparer.Ordinal);

    public HubChannelMembership(TimeProvider? clock = null, TimeSpan? tombstoneTtl = null)
    {
        _clock = clock ?? TimeProvider.System;
        _tombstoneTtl = tombstoneTtl ?? DefaultTombstoneTtl;
    }

    /// <summary>
    /// Record that <paramref name="connectionId"/> joined <paramref name="channel"/>,
    /// carrying <paramref name="identity"/> (null for public/ops channels). Idempotent:
    /// a re-join overwrites the stored identity with the latest decision. Returns
    /// <c>false</c> — and records nothing — when the connection was recently dropped
    /// (a join resumed after its disconnect); callers must then skip the dependent
    /// presence writes too.
    /// </summary>
    public bool Join(string connectionId, string channel, string? identity)
    {
        if (IsTombstoned(connectionId))
        {
            return false;
        }

        var channels = _byConnection.GetOrAdd(
            connectionId, _ => new ConcurrentDictionary<string, string?>(StringComparer.Ordinal));
        channels[channel] = identity;

        // Drop may have raced between the tombstone check and the write, re-creating the
        // map here; undo so a late join cannot leak a membership for a dead connection.
        if (IsTombstoned(connectionId))
        {
            _byConnection.TryRemove(connectionId, out _);
            return false;
        }

        return true;
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

    /// <summary>
    /// A point-in-time snapshot of every (channel, connectionId) membership on this
    /// instance — the authoritative worklist for the eviction sweep.
    /// </summary>
    public IReadOnlyList<(string Channel, string ConnectionId)> Snapshot()
    {
        var result = new List<(string, string)>();
        foreach (var (connectionId, channels) in _byConnection)
        {
            foreach (var channel in channels.Keys)
            {
                result.Add((channel, connectionId));
            }
        }

        return result;
    }

    /// <summary>Forget every membership for a connection (called on disconnect).</summary>
    public void Drop(string connectionId)
    {
        // Tombstone BEFORE removing so a join racing this disconnect observes it.
        _tombstones[connectionId] = _clock.GetUtcNow() + _tombstoneTtl;
        _byConnection.TryRemove(connectionId, out _);

        // Amortized tombstone cleanup — connection ids are never reused, so expired
        // tombstones are pure garbage.
        var now = _clock.GetUtcNow();
        foreach (var (id, until) in _tombstones)
        {
            if (now >= until)
            {
                _tombstones.TryRemove(id, out _);
            }
        }
    }

    private bool IsTombstoned(string connectionId)
    {
        if (_tombstones.TryGetValue(connectionId, out var until))
        {
            if (_clock.GetUtcNow() < until)
            {
                return true;
            }

            _tombstones.TryRemove(connectionId, out _);
        }

        return false;
    }
}
