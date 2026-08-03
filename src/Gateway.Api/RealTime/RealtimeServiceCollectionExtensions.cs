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

        // The dashboard's ops:* channel authorization policy
        // (GatewayHub.OpsChannelPolicy) is defined by AddManagementAuthentication
        // (§4.2, §5), so the hub gate shares the exact Cognito-group requirement the
        // /mgmt endpoints enforce.
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
