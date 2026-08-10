using System.Collections.Concurrent;

namespace Gateway.Api.RealTime;

/// <summary>
/// A delegated-auth decision for one <c>(connectionId, channel)</c> pair (task #594):
/// whether the join was admitted and, on an allow, the opaque identity string the
/// owning service returned (stored for phase 3 presence/messaging — nothing else
/// consumes it yet).
/// </summary>
public readonly record struct ChannelAuthDecision(bool Allowed, string? Identity)
{
    /// <summary>An admit decision carrying the service-supplied identity (may be null).</summary>
    public static ChannelAuthDecision Allow(string? identity) => new(true, identity);

    /// <summary>A reject decision. Carries no identity.</summary>
    public static readonly ChannelAuthDecision Deny = new(false, null);
}

/// <summary>
/// Per-connection cache of delegated channel-auth decisions (task #594, requirement 4).
/// An <b>allow</b> is cached for the whole lifetime of the connection so a reconnect's
/// group re-adds or a client that repeatedly re-joins never re-hits the owning app; a
/// <b>deny</b> is cached only briefly (~10s) so a client cannot be locked out for the
/// connection's life yet a brute-force loop still can't hammer the app every frame.
/// Every decision for a connection is dropped when it disconnects
/// (<see cref="Drop"/>, called from <c>GatewayHub.OnDisconnectedAsync</c>).
/// <para>
/// Singleton and thread-safe: the hub is instantiated per invocation, so the cache
/// that must outlive a single <c>JoinChannel</c> lives here, not on the hub.
/// </para>
/// </summary>
public sealed class ChannelAuthDecisionCache
{
    /// <summary>How long a deny is remembered before the next join re-hits the app.</summary>
    public static readonly TimeSpan DefaultDenialTtl = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _clock;
    private readonly TimeSpan _denialTtl;

    // connectionId -> (channel -> entry). Nested maps so a whole connection's decisions
    // drop in one operation on disconnect without scanning the entire cache.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Entry>> _byConnection =
        new(StringComparer.Ordinal);

    public ChannelAuthDecisionCache(TimeProvider? clock = null, TimeSpan? denialTtl = null)
    {
        _clock = clock ?? TimeProvider.System;
        _denialTtl = denialTtl ?? DefaultDenialTtl;
    }

    /// <summary>
    /// A still-valid cached decision for this pair, or null on a miss. An allow never
    /// expires (until the connection drops); a deny past its short TTL is treated as a
    /// miss and evicted so the next join re-consults the owning service.
    /// </summary>
    public ChannelAuthDecision? TryGet(string connectionId, string channel)
    {
        if (!_byConnection.TryGetValue(connectionId, out var channels) ||
            !channels.TryGetValue(channel, out var entry))
        {
            return null;
        }

        if (entry.Decision.Allowed)
        {
            return entry.Decision;
        }

        // A deny: honour it only within the short TTL, then evict and re-hit the app.
        if (_clock.GetUtcNow() < entry.ExpiresAt)
        {
            return entry.Decision;
        }

        channels.TryRemove(channel, out _);
        return null;
    }

    /// <summary>Cache an admit for the lifetime of the connection.</summary>
    public void StoreAllow(string connectionId, string channel, string? identity) =>
        Store(connectionId, channel, new Entry(ChannelAuthDecision.Allow(identity), DateTimeOffset.MaxValue));

    /// <summary>Cache a reject for the short denial TTL (brute-force blunt).</summary>
    public void StoreDeny(string connectionId, string channel) =>
        Store(connectionId, channel, new Entry(ChannelAuthDecision.Deny, _clock.GetUtcNow() + _denialTtl));

    /// <summary>Forget every decision for a connection (called on disconnect).</summary>
    public void Drop(string connectionId) => _byConnection.TryRemove(connectionId, out _);

    private void Store(string connectionId, string channel, Entry entry)
    {
        var channels = _byConnection.GetOrAdd(
            connectionId, _ => new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal));
        channels[channel] = entry;
    }

    private readonly record struct Entry(ChannelAuthDecision Decision, DateTimeOffset ExpiresAt);
}
