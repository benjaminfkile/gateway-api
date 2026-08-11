namespace Gateway.Api.RealTime;

/// <summary>
/// A single classic token-bucket rate limiter (task #611). The bucket holds up to
/// <see cref="_capacity"/> (burst) tokens and refills continuously at
/// <see cref="_refillPerSecond"/> tokens/second; each accepted operation costs one
/// token. Over budget → <see cref="TryTake"/> returns <c>false</c> and reports how long
/// the caller must wait for the next token.
/// <para>
/// This is a pure in-process primitive with an injectable <see cref="TimeProvider"/>, so
/// every rate limit built on it is deterministic under a fake clock and needs no Redis,
/// AWS, or wall-clock sleep to unit-test. The gateway's rate limits are all per-connection
/// or per-service on a <b>single instance</b> — a hub connection lives on exactly one
/// instance and an <c>/internal/publish</c> call is limited by whichever instance served
/// it — so they are correct instance-local without a shared backplane; a fleet-wide limit
/// would layer a Redis-backed bucket behind the same call sites, following the SignalR
/// backplane pattern.
/// </para>
/// Thread-safe: all state mutation happens under <see cref="_gate"/>.
/// </summary>
public sealed class TokenBucket
{
    private readonly double _capacity;
    private readonly double _refillPerSecond;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();

    private double _tokens;
    private DateTimeOffset _lastRefill;

    /// <param name="ratePerSecond">Sustained refill rate in tokens/second (must be &gt; 0).</param>
    /// <param name="burst">Bucket capacity — the largest burst allowed (must be &gt;= 1).</param>
    /// <param name="clock">Clock for refill accounting; defaults to the system clock.</param>
    public TokenBucket(double ratePerSecond, double burst, TimeProvider? clock = null)
    {
        if (ratePerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ratePerSecond), ratePerSecond, "rate must be positive.");
        }

        if (burst < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(burst), burst, "burst must be at least 1.");
        }

        _refillPerSecond = ratePerSecond;
        _capacity = burst;
        _clock = clock ?? TimeProvider.System;

        // Start full so the first burst is honoured immediately.
        _tokens = burst;
        _lastRefill = _clock.GetUtcNow();
    }

    /// <summary>
    /// Try to spend one token. Returns <c>true</c> and consumes a token when one is
    /// available; otherwise returns <c>false</c> and sets <paramref name="retryAfter"/>
    /// to the shortest wait until the bucket has a token again (never negative).
    /// </summary>
    public bool TryTake(out TimeSpan retryAfter)
    {
        lock (_gate)
        {
            Refill();

            if (_tokens >= 1d)
            {
                _tokens -= 1d;
                retryAfter = TimeSpan.Zero;
                return true;
            }

            // Seconds until the deficit (one whole token) is refilled.
            var deficit = 1d - _tokens;
            retryAfter = TimeSpan.FromSeconds(deficit / _refillPerSecond);
            return false;
        }
    }

    private void Refill()
    {
        var now = _clock.GetUtcNow();
        var elapsed = (now - _lastRefill).TotalSeconds;
        if (elapsed <= 0)
        {
            return;
        }

        _tokens = Math.Min(_capacity, _tokens + (elapsed * _refillPerSecond));
        _lastRefill = now;
    }
}
