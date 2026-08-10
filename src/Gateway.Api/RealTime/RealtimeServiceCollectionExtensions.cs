using Microsoft.Extensions.DependencyInjection;
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
        //
        // Pin the real-time timeouts instead of riding SignalR's framework defaults
        // — these values are part of the contract with the dashboard client, which
        // matches its own ServerTimeout/KeepAlive to them:
        //   KeepAliveInterval    15s — server→client ping cadence.
        //   ClientTimeoutInterval 30s — drop a connection after 2 missed pings.
        //   HandshakeTimeout      15s — cap the initial negotiate handshake.
        // The ALB idle timeout (900s) far exceeds the 15s keepalive, so it is the
        // app-layer ping — not the load balancer — that holds the WebSocket open.
        var signalR = services.AddSignalR(options =>
        {
            options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
            options.HandshakeTimeout = TimeSpan.FromSeconds(15);
        });

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

        // The single envelope publisher every server-side broadcast goes through
        // (§4.2 ChannelEvent wire contract). Singleton — it holds only the hub
        // context and a logger, both singletons.
        services.TryAddSingleton<IChannelEventPublisher, ChannelEventPublisher>();

        // Channel-ownership resolver (task #593): the hub's JoinChannel and the
        // internal publish endpoint both consult it to map a channel prefix onto the
        // owning manifest service. Singleton with a short-TTL cache over the manifest
        // store, reached through a scope factory like the reconciler.
        services.TryAddSingleton<IChannelOwnershipResolver>(sp =>
            new ManifestChannelOwnershipResolver(sp.GetRequiredService<IServiceScopeFactory>()));

        return services;
    }
}
