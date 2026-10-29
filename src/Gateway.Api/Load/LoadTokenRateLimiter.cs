using System.Collections.Concurrent;
using Gateway.Api.RealTime;

namespace Gateway.Api.Load;

/// <summary>
/// Per-source-address throttle for the two run-token endpoints
/// (<c>POST /mgmt/load/runs/{id}/checkin</c> and <c>/stats</c>, docs/load.md section
/// 8): one <see cref="TokenBucket"/> per address, 5 requests per second with a burst
/// of 10 by default. Over budget the endpoint answers <c>429</c> with
/// <c>Retry-After</c>.
/// <para>
/// Singleton, thread-safe and instance-local like <see cref="PublishRateLimiter"/>.
/// The address map is cleared when it passes <see cref="MaxAddresses"/> entries, so a
/// spray of addresses cannot grow it without bound (a cleared address starts with a
/// full bucket again).
/// </para>
/// </summary>
public sealed class LoadTokenRateLimiter
{
    /// <summary>Default sustained rate per address, requests per second.</summary>
    public const double DefaultRate = 5;

    /// <summary>Default burst per address.</summary>
    public const double DefaultBurst = 10;

    /// <summary>Most addresses tracked before the map is cleared.</summary>
    public const int MaxAddresses = 10_000;

    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new(StringComparer.Ordinal);
    private readonly double _rate;
    private readonly double _burst;
    private readonly TimeProvider _clock;

    public LoadTokenRateLimiter(double rate = DefaultRate, double burst = DefaultBurst, TimeProvider? clock = null)
    {
        _rate = rate;
        _burst = burst;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Try to spend one token for <paramref name="address"/>. False when the address is
    /// over budget, with <paramref name="retryAfter"/> the wait until the next token.
    /// </summary>
    public bool TryTake(string address, out TimeSpan retryAfter)
    {
        if (_buckets.Count >= MaxAddresses && !_buckets.ContainsKey(address))
        {
            _buckets.Clear();
        }

        var bucket = _buckets.GetOrAdd(address, _ => new TokenBucket(_rate, _burst, _clock));
        return bucket.TryTake(out retryAfter);
    }
}
