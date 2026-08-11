using System.Collections.Concurrent;

namespace Gateway.Api.RealTime;

/// <summary>
/// Per-service token-bucket throttle for <c>POST /internal/publish</c> (task #611, item 4).
/// One bucket per owning service, keyed on the channel prefix, so a single chatty service
/// cannot exhaust the hub for everyone; defaults come from <see cref="RealtimeRateLimitOptions"/>
/// (50 publishes/s, burst 100). Over budget → the endpoint returns <c>429</c> with a
/// <c>Retry-After</c> derived from the reported wait.
/// <para>
/// A service bucket is created lazily on first publish and never removed — the number of
/// distinct services is bounded by the manifest, so the map cannot grow unbounded.
/// Singleton, thread-safe, and instance-local by design (each instance throttles the
/// publishes it serves); a fleet-wide cap would layer a Redis-backed bucket behind this
/// same call site, following the SignalR backplane pattern.
/// </para>
/// </summary>
public sealed class PublishRateLimiter
{
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new(StringComparer.Ordinal);
    private readonly double _rate;
    private readonly double _burst;
    private readonly TimeProvider _clock;

    public PublishRateLimiter(RealtimeRateLimitOptions options, TimeProvider? clock = null)
    {
        _rate = options.PublishRate;
        _burst = options.PublishBurst;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Try to spend one publish token for <paramref name="service"/>. Returns <c>false</c>
    /// when the service is over its budget and sets <paramref name="retryAfter"/> to the
    /// shortest wait until the next token (for the <c>Retry-After</c> header).
    /// </summary>
    public bool TryTake(string service, out TimeSpan retryAfter)
    {
        var bucket = _buckets.GetOrAdd(service, _ => new TokenBucket(_rate, _burst, _clock));
        return bucket.TryTake(out retryAfter);
    }
}
