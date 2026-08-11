using Gateway.Api.Proxy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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

        // The one short-TTL manifest snapshot shared by every realtime consumer that
        // would otherwise read the manifest per request (task #607): channel ownership
        // below and the /hub CORS origin cache both project off it, so the instance runs
        // at most one full-manifest read per TTL — serve-stale and single-flight, so a
        // transient DB blip never 500s a join, publish, or preflight.
        services.TryAddSingleton(sp => new ManifestSnapshotCache(
            sp.GetRequiredService<IServiceScopeFactory>(),
            ttl: null,
            clock: null,
            logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger<ManifestSnapshotCache>()));

        // Channel-ownership resolver (task #593): the hub's JoinChannel and the
        // internal publish endpoint both consult it to map a channel prefix onto the
        // owning manifest service. Projects the ownership map off the shared snapshot.
        services.TryAddSingleton<IChannelOwnershipResolver>(sp =>
            new ManifestChannelOwnershipResolver(sp.GetRequiredService<ManifestSnapshotCache>()));

        // Delegated channel auth (task #594): a private channel's join is authorized by
        // the owning service's own callback, not the gateway. A dedicated HttpClient
        // bounded at 2s (also enforced per-callback in HttpChannelAuthClient) reaches the
        // service the same way the health prober does; the decision cache (singleton so
        // it outlives a per-invocation hub) remembers allows for the connection lifetime
        // and denies briefly. TryAdd so a test can substitute a fake auth client first.
        //
        // The auth client reaches services through the same address resolution the health
        // prober and proxy use; TryAdd its two collaborators here so the realtime layer is
        // self-contained (a realtime-only host that never calls AddManifestProxy — the
        // internal-publish tests — still resolves the hub). AddManifestProxy TryAdds the
        // same defaults, so whichever runs first wins and there is no double registration.
        services.TryAddSingleton<IServiceAddressResolver, HostLoopbackAddressResolver>();
        services.TryAddSingleton<ServiceHostPortMap>();

        services.AddHttpClient(HttpChannelAuthClient.HttpClientName, client =>
        {
            client.Timeout = HttpChannelAuthClient.CallbackTimeout;
        });
        services.TryAddSingleton<IChannelAuthClient, HttpChannelAuthClient>();
        services.TryAddSingleton<ChannelAuthDecisionCache>();

        return services;
    }
}
