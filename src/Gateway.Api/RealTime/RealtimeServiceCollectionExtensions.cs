using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gateway.Api.RealTime;

/// <summary>
/// Registration for the real-time hub (tech-spec §4.2). Wires SignalR, the
/// optional Redis backplane, the <see cref="GatewayHub.OpsChannelPolicy"/>
/// authorization hook, and the internal-listener options. Shared by the host and
/// integration tests so both configure the hub identically.
/// </summary>
public static class RealtimeServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayRealtime(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddSingleton(InternalListenerOptions.FromConfiguration(configuration));

        var redis = RedisBackplaneOptions.FromConfiguration(configuration);
        services.TryAddSingleton(redis);

        // Authorization hook for the dashboard's ops:* channels. Today the policy
        // only requires an authenticated principal — with no auth handler wired
        // yet, that rejects every anonymous connection, which is the required
        // behaviour until the Cognito task makes this a real JWT gate (§4.2, §5).
        services.AddAuthorization(options =>
        {
            options.AddPolicy(GatewayHub.OpsChannelPolicy, policy =>
                policy.RequireAuthenticatedUser());
        });

        var signalR = services.AddSignalR();

        // Backplane only when GATEWAY_REDIS_ENDPOINT is set (§4.2). Otherwise the
        // hub runs single-node with no Redis dependency — correct for one
        // instance, tests, and local dev.
        if (redis.Enabled)
        {
            signalR.AddStackExchangeRedis(options =>
            {
                options.Configuration.EndPoints.Add(redis.Endpoint!);
                options.Configuration.Ssl = redis.UseSsl;
            });
        }

        return services;
    }
}
