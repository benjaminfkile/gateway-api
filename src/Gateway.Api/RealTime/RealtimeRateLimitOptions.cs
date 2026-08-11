namespace Gateway.Api.RealTime;

/// <summary>
/// Environment-driven rate-limit knobs for the real-time full-duplex path (task #611):
/// the per-connection <c>SendToChannel</c> message limit and the per-service
/// <c>/internal/publish</c> throttle. Both are classic token buckets (<see cref="TokenBucket"/>)
/// with a sustained rate and a burst; defaults are generous enough that a well-behaved app
/// never notices them and only a runaway/abusive source is clipped. Read once at startup —
/// env var wins, then config, then the default.
/// </summary>
public sealed class RealtimeRateLimitOptions
{
    /// <summary>Env/config key for the per-connection message rate (messages/second).</summary>
    public const string MessageRateEnvVar = "GATEWAY_REALTIME_MSG_RATE";

    /// <summary>Env/config key for the per-connection message burst (bucket capacity).</summary>
    public const string MessageBurstEnvVar = "GATEWAY_REALTIME_MSG_BURST";

    /// <summary>Env/config key for the per-service publish rate (publishes/second).</summary>
    public const string PublishRateEnvVar = "GATEWAY_REALTIME_PUBLISH_RATE";

    /// <summary>Env/config key for the per-service publish burst (bucket capacity).</summary>
    public const string PublishBurstEnvVar = "GATEWAY_REALTIME_PUBLISH_BURST";

    /// <summary>Default sustained per-connection message rate: 10 messages/second.</summary>
    public const double DefaultMessageRate = 10d;

    /// <summary>Default per-connection message burst: 20.</summary>
    public const double DefaultMessageBurst = 20d;

    /// <summary>Default sustained per-service publish rate: 50 publishes/second.</summary>
    public const double DefaultPublishRate = 50d;

    /// <summary>Default per-service publish burst: 100.</summary>
    public const double DefaultPublishBurst = 100d;

    /// <summary>Env/config key for the per-connection hub-invocation rate (invocations/second).</summary>
    public const string InvocationRateEnvVar = "GATEWAY_REALTIME_INVOKE_RATE";

    /// <summary>Env/config key for the per-connection hub-invocation burst (bucket capacity).</summary>
    public const string InvocationBurstEnvVar = "GATEWAY_REALTIME_INVOKE_BURST";

    /// <summary>
    /// Default sustained per-connection hub-invocation rate: 20/second. Covers EVERY
    /// client-callable hub method (review finding: only SendToChannel was limited, so a
    /// public join/leave spam loop could drive unbounded Redis/backplane load) while
    /// staying far above any legitimate client — a reconnecting dashboard re-joining a
    /// handful of channels is a burst of single digits.
    /// </summary>
    public const double DefaultInvocationRate = 20d;

    /// <summary>Default per-connection hub-invocation burst: 40.</summary>
    public const double DefaultInvocationBurst = 40d;

    /// <summary>Per-connection sustained hub-invocation rate (invocations/second).</summary>
    public double InvocationRate { get; init; } = DefaultInvocationRate;

    /// <summary>Per-connection hub-invocation burst (token-bucket capacity).</summary>
    public double InvocationBurst { get; init; } = DefaultInvocationBurst;

    /// <summary>Per-connection sustained message rate (messages/second).</summary>
    public double MessageRate { get; init; } = DefaultMessageRate;

    /// <summary>Per-connection message burst (token-bucket capacity).</summary>
    public double MessageBurst { get; init; } = DefaultMessageBurst;

    /// <summary>Per-service sustained publish rate (publishes/second).</summary>
    public double PublishRate { get; init; } = DefaultPublishRate;

    /// <summary>Per-service publish burst (token-bucket capacity).</summary>
    public double PublishBurst { get; init; } = DefaultPublishBurst;

    /// <summary>
    /// Read the rate-limit configuration. A missing, blank, non-numeric, or non-positive
    /// value falls back to the documented default so a fat-fingered env var can never
    /// disable the limiter or crash startup.
    /// </summary>
    public static RealtimeRateLimitOptions FromConfiguration(IConfiguration configuration) => new()
    {
        MessageRate = Read(configuration, MessageRateEnvVar, DefaultMessageRate, min: double.Epsilon),
        // Bursts feed TokenBucket's capacity, whose constructor requires >= 1 — a
        // fractional burst passing validation here would crash every rate-limited
        // operation at runtime instead of startup (review finding), so the floor for
        // burst values is 1, not "positive".
        MessageBurst = Read(configuration, MessageBurstEnvVar, DefaultMessageBurst, min: 1),
        PublishRate = Read(configuration, PublishRateEnvVar, DefaultPublishRate, min: double.Epsilon),
        PublishBurst = Read(configuration, PublishBurstEnvVar, DefaultPublishBurst, min: 1),
        InvocationRate = Read(configuration, InvocationRateEnvVar, DefaultInvocationRate, min: double.Epsilon),
        InvocationBurst = Read(configuration, InvocationBurstEnvVar, DefaultInvocationBurst, min: 1),
    };

    private static double Read(IConfiguration configuration, string key, double fallback, double min)
    {
        var raw = Environment.GetEnvironmentVariable(key) ?? configuration[key];
        if (!string.IsNullOrWhiteSpace(raw)
            && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            && value >= min)
        {
            return value;
        }

        return fallback;
    }
}
