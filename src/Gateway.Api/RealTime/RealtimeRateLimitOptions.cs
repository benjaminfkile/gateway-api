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
        MessageRate = Read(configuration, MessageRateEnvVar, DefaultMessageRate),
        MessageBurst = Read(configuration, MessageBurstEnvVar, DefaultMessageBurst),
        PublishRate = Read(configuration, PublishRateEnvVar, DefaultPublishRate),
        PublishBurst = Read(configuration, PublishBurstEnvVar, DefaultPublishBurst),
    };

    private static double Read(IConfiguration configuration, string key, double fallback)
    {
        var raw = Environment.GetEnvironmentVariable(key) ?? configuration[key];
        if (!string.IsNullOrWhiteSpace(raw)
            && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            && value > 0)
        {
            return value;
        }

        return fallback;
    }
}
