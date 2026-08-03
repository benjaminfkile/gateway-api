namespace Gateway.Api.RealTime;

/// <summary>
/// Configuration for the optional SignalR Redis backplane (tech-spec §4.2). The
/// backplane fans hub messages out across every gateway instance so an event
/// published on one box reaches clients connected to any other. It is enabled
/// <b>only</b> when <c>GATEWAY_REDIS_ENDPOINT</c> is set; otherwise the hub runs
/// single-node (correct for one instance, tests, and local dev) with no Redis
/// dependency. TLS defaults on (<c>GATEWAY_REDIS_SSL</c>, default <c>true</c>).
/// </summary>
public sealed class RedisBackplaneOptions
{
    /// <summary>Environment variable / config key holding the Redis endpoint (<c>host:port</c>).</summary>
    public const string EndpointEnvVar = "GATEWAY_REDIS_ENDPOINT";

    /// <summary>Environment variable / config key toggling TLS to Redis (default <c>true</c>).</summary>
    public const string SslEnvVar = "GATEWAY_REDIS_SSL";

    /// <summary>Redis endpoint (<c>host:port</c>); <c>null</c>/empty means no backplane.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Whether to connect to Redis over TLS.</summary>
    public bool UseSsl { get; init; } = true;

    /// <summary>True when a backplane endpoint is configured.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(Endpoint);

    /// <summary>
    /// Read the backplane configuration. The endpoint is absent by default, so
    /// the hub is backplane-free unless <c>GATEWAY_REDIS_ENDPOINT</c> is set. SSL
    /// defaults to <c>true</c> and is only disabled by an explicit falsey value.
    /// </summary>
    public static RedisBackplaneOptions FromConfiguration(IConfiguration configuration)
    {
        var endpoint = Environment.GetEnvironmentVariable(EndpointEnvVar)
            ?? configuration[EndpointEnvVar];

        var sslValue = Environment.GetEnvironmentVariable(SslEnvVar)
            ?? configuration[SslEnvVar];
        var useSsl = string.IsNullOrWhiteSpace(sslValue)
            || !(string.Equals(sslValue, "false", StringComparison.OrdinalIgnoreCase)
                || sslValue == "0");

        return new RedisBackplaneOptions
        {
            Endpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint,
            UseSsl = useSsl,
        };
    }
}
