namespace Gateway.Api.RealTime;

/// <summary>
/// One connection's presence in a channel: the SignalR <paramref name="ConnectionId"/>,
/// the opaque owner-supplied <paramref name="Identity"/> (null for public/ops channels —
/// it is whatever the service's delegated-auth callback returned, task #594), and the
/// instant it joined. Handed back verbatim by <see cref="IPresenceRegistry.ListAsync"/>
/// and the owner presence API (task #612).
/// </summary>
public sealed record PresenceEntry(string ConnectionId, string? Identity, DateTimeOffset JoinedAt);

/// <summary>
/// One <c>(channel, connectionId)</c> membership <b>owned by this instance</b>, enumerated by
/// <see cref="IPresenceRegistry.LocalMembershipsAsync"/> to drive the eviction sweep (task
/// #613). Deliberately instance-local, not the fleet union <see cref="IPresenceRegistry.ListAsync"/>
/// returns: eviction removes a SignalR group membership and consults the delegated-auth
/// decision cache, both of which live on the instance the connection is pinned to, so each
/// instance evicts only its own connections.
/// </summary>
public sealed record ChannelMembership(string Channel, string ConnectionId);

/// <summary>
/// "Who is in this channel" as a first-class, workload-agnostic capability (tech-spec
/// §4.2, task #612). The hub records a membership on every join and drops it on
/// leave/disconnect; the owner presence API and the coalesced <c>presence</c> events read
/// it back. Presence is <b>best-effort</b>: a registry failure is logged and never fails a
/// join (delivery is not gated on presence), so callers treat it as advisory and reconcile
/// via <see cref="ListAsync"/>.
/// <para>
/// Two implementations, selected exactly like the SignalR backplane
/// (<see cref="RedisBackplaneOptions.Enabled"/>): <see cref="InMemoryPresenceRegistry"/>
/// (default — correct and complete for a single instance) and a Redis-backed one that
/// unions presence across a fleet. The interface is deliberately async so the Redis
/// implementation fits without changing call sites.
/// </para>
/// </summary>
public interface IPresenceRegistry
{
    /// <summary>
    /// Record that <paramref name="connectionId"/> is present in <paramref name="channel"/>
    /// carrying <paramref name="identity"/> (null for public/ops). Idempotent: a re-add
    /// keeps the original <see cref="PresenceEntry.JoinedAt"/> and refreshes the identity.
    /// </summary>
    Task AddAsync(string channel, string connectionId, string? identity, CancellationToken ct = default);

    /// <summary>Remove a single channel membership (from <c>LeaveChannel</c>).</summary>
    Task RemoveAsync(string channel, string connectionId, CancellationToken ct = default);

    /// <summary>
    /// Remove every membership for <paramref name="connectionId"/> (the disconnect sweep)
    /// and return the channels it was actually removed from, so the caller can emit a
    /// <c>presence</c> leave event on each.
    /// </summary>
    Task<IReadOnlyCollection<string>> RemoveConnectionAsync(string connectionId, CancellationToken ct = default);

    /// <summary>Every connection currently present in <paramref name="channel"/>.</summary>
    Task<IReadOnlyList<PresenceEntry>> ListAsync(string channel, CancellationToken ct = default);

    /// <summary>How many connections are currently present in <paramref name="channel"/>.</summary>
    Task<int> CountAsync(string channel, CancellationToken ct = default);

    /// <summary>
    /// Every <c>(channel, connectionId)</c> membership this instance owns — the input to the
    /// eviction sweep (task #613). Instance-local by design (see <see cref="ChannelMembership"/>):
    /// the in-memory registry owns every connection it sees, and the Redis one returns only the
    /// rows written by this instance, never the fleet union.
    /// </summary>
    Task<IReadOnlyList<ChannelMembership>> LocalMembershipsAsync(CancellationToken ct = default);
}
