using System.Collections.Concurrent;

namespace Gateway.Api.RealTime;

/// <summary>
/// Per-connection token-bucket limiter for <c>SendToChannel</c> (task #611, rule c). One
/// bucket per hub connection, <b>shared across every channel</b> the connection sends on,
/// so a client cannot dodge the limit by spraying across channels. Defaults come from
/// <see cref="RealtimeRateLimitOptions"/> (10 msg/s, burst 20). A connection's bucket is
/// created lazily on its first send and dropped on disconnect (<see cref="Drop"/>, called
/// from <c>GatewayHub.OnDisconnectedAsync</c>) so the map never grows for the process
/// lifetime — connection ids are never reused.
/// <para>
/// Singleton and thread-safe: the hub is instantiated per invocation, so the state that
/// must outlive a single <c>SendToChannel</c> lives here, not on the hub. Instance-local
/// by design — a connection lives on exactly one gateway instance, so a per-instance
/// bucket is the whole truth for that connection (<see cref="TokenBucket"/>).
/// </para>
/// </summary>
public sealed class MessageRateLimiter
{
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new(StringComparer.Ordinal);
    private readonly double _rate;
    private readonly double _burst;
    private readonly TimeProvider _clock;

    public MessageRateLimiter(RealtimeRateLimitOptions options, TimeProvider? clock = null)
    {
        _rate = options.MessageRate;
        _burst = options.MessageBurst;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Try to spend one message token for <paramref name="connectionId"/>. Returns
    /// <c>false</c> when the connection is over its budget.
    /// </summary>
    public bool TryTake(string connectionId)
    {
        var bucket = _buckets.GetOrAdd(connectionId, _ => new TokenBucket(_rate, _burst, _clock));
        return bucket.TryTake(out _);
    }

    /// <summary>Forget a connection's bucket (called on disconnect).</summary>
    public void Drop(string connectionId) => _buckets.TryRemove(connectionId, out _);
}
