using Gateway.Api.RealTime;
using Microsoft.Extensions.Configuration;

namespace Gateway.Api.Tests;

/// <summary>
/// Unit coverage for the task #611 rate limiters — the <see cref="TokenBucket"/> primitive,
/// the per-connection <see cref="MessageRateLimiter"/>, and the per-service
/// <see cref="PublishRateLimiter"/> — all driven by a fake clock so the burst, the sustained
/// rate, and the recovery are deterministic offline (no Redis, no wall-clock sleep).
/// </summary>
public class RealtimeRateLimitTests
{
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void TokenBucket_AllowsBurst_ThenThrottles()
    {
        var clock = new TestClock();
        var bucket = new TokenBucket(ratePerSecond: 10, burst: 20, clock);

        // The full burst is honoured immediately.
        for (var i = 0; i < 20; i++)
        {
            Assert.True(bucket.TryTake(out _), $"burst token {i} should be granted");
        }

        // The 21st is over budget and reports a positive wait.
        Assert.False(bucket.TryTake(out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void TokenBucket_Refills_AtSustainedRate()
    {
        var clock = new TestClock();
        var bucket = new TokenBucket(ratePerSecond: 10, burst: 20, clock);

        // Drain the bucket.
        for (var i = 0; i < 20; i++)
        {
            Assert.True(bucket.TryTake(out _));
        }

        Assert.False(bucket.TryTake(out _));

        // After 1s at 10 tokens/s, ten more are available (not the full burst).
        clock.Now += TimeSpan.FromSeconds(1);
        for (var i = 0; i < 10; i++)
        {
            Assert.True(bucket.TryTake(out _), $"refilled token {i} should be granted");
        }

        Assert.False(bucket.TryTake(out _));
    }

    [Fact]
    public void TokenBucket_RefillIsCappedAtBurst()
    {
        var clock = new TestClock();
        var bucket = new TokenBucket(ratePerSecond: 10, burst: 20, clock);

        for (var i = 0; i < 20; i++)
        {
            Assert.True(bucket.TryTake(out _));
        }

        // Idle for a long time: the bucket refills only up to the burst capacity, not beyond.
        clock.Now += TimeSpan.FromMinutes(5);
        for (var i = 0; i < 20; i++)
        {
            Assert.True(bucket.TryTake(out _), $"post-idle token {i} should be granted");
        }

        Assert.False(bucket.TryTake(out _));
    }

    [Fact]
    public void MessageRateLimiter_IsPerConnection_SharedAcrossChannels()
    {
        var clock = new TestClock();
        var options = new RealtimeRateLimitOptions { MessageRate = 10, MessageBurst = 3 };
        var limiter = new MessageRateLimiter(options, clock);

        // conn-1 spends its whole burst; the limit is per connection, not per channel.
        Assert.True(limiter.TryTake("conn-1"));
        Assert.True(limiter.TryTake("conn-1"));
        Assert.True(limiter.TryTake("conn-1"));
        Assert.False(limiter.TryTake("conn-1"));

        // A different connection has its own independent budget.
        Assert.True(limiter.TryTake("conn-2"));

        // conn-1 recovers after the clock advances.
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.True(limiter.TryTake("conn-1"));
    }

    [Fact]
    public void MessageRateLimiter_Drop_ResetsBudget()
    {
        var clock = new TestClock();
        var options = new RealtimeRateLimitOptions { MessageRate = 10, MessageBurst = 1 };
        var limiter = new MessageRateLimiter(options, clock);

        Assert.True(limiter.TryTake("conn-1"));
        Assert.False(limiter.TryTake("conn-1"));

        // Dropping the connection (disconnect) discards its bucket; a reused id starts fresh.
        limiter.Drop("conn-1");
        Assert.True(limiter.TryTake("conn-1"));
    }

    [Fact]
    public void PublishRateLimiter_IsPerService_AndReportsRetryAfter()
    {
        var clock = new TestClock();
        var options = new RealtimeRateLimitOptions { PublishRate = 50, PublishBurst = 2 };
        var limiter = new PublishRateLimiter(options, clock);

        Assert.True(limiter.TryTake("svc-a", out _));
        Assert.True(limiter.TryTake("svc-a", out _));
        Assert.False(limiter.TryTake("svc-a", out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);

        // svc-b has its own budget.
        Assert.True(limiter.TryTake("svc-b", out _));

        // svc-a recovers after enough time for one token (1/50s).
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.True(limiter.TryTake("svc-a", out _));
    }

    [Fact]
    public void Options_DefaultsAndEnvOverrides()
    {
        var defaults = RealtimeRateLimitOptions.FromConfiguration(
            new ConfigurationBuilder().Build());
        Assert.Equal(RealtimeRateLimitOptions.DefaultMessageRate, defaults.MessageRate);
        Assert.Equal(RealtimeRateLimitOptions.DefaultMessageBurst, defaults.MessageBurst);
        Assert.Equal(RealtimeRateLimitOptions.DefaultPublishRate, defaults.PublishRate);
        Assert.Equal(RealtimeRateLimitOptions.DefaultPublishBurst, defaults.PublishBurst);

        var overridden = RealtimeRateLimitOptions.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [RealtimeRateLimitOptions.MessageRateEnvVar] = "5",
                [RealtimeRateLimitOptions.MessageBurstEnvVar] = "7",
                [RealtimeRateLimitOptions.PublishRateEnvVar] = "100",
                [RealtimeRateLimitOptions.PublishBurstEnvVar] = "200",
            }).Build());
        Assert.Equal(5, overridden.MessageRate);
        Assert.Equal(7, overridden.MessageBurst);
        Assert.Equal(100, overridden.PublishRate);
        Assert.Equal(200, overridden.PublishBurst);
    }

    [Fact]
    public void Options_IgnoresGarbageAndNonPositive()
    {
        var options = RealtimeRateLimitOptions.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [RealtimeRateLimitOptions.MessageRateEnvVar] = "not-a-number",
                [RealtimeRateLimitOptions.MessageBurstEnvVar] = "0",
                [RealtimeRateLimitOptions.PublishRateEnvVar] = "-5",
            }).Build());

        // A fat-fingered or hostile value can never disable the limiter — it falls back.
        Assert.Equal(RealtimeRateLimitOptions.DefaultMessageRate, options.MessageRate);
        Assert.Equal(RealtimeRateLimitOptions.DefaultMessageBurst, options.MessageBurst);
        Assert.Equal(RealtimeRateLimitOptions.DefaultPublishRate, options.PublishRate);
    }
}
