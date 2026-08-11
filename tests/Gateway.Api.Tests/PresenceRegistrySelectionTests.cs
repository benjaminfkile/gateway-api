using Gateway.Api.RealTime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Gateway.Api.Tests;

/// <summary>
/// The presence-registry selection gate (task #612): it MIRRORS the SignalR backplane gate —
/// Redis endpoint set → <see cref="RedisPresenceRegistry"/> (plus its reaper hosted service),
/// unset → <see cref="InMemoryPresenceRegistry"/>. Asserted against the service registrations
/// without connecting to Redis (the Redis case only inspects descriptors — building the
/// provider would try to dial a real server, which the offline container forbids).
/// </summary>
public class PresenceRegistrySelectionTests
{
    private static IServiceCollection Wire(IDictionary<string, string?> config)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGatewayRealtime(configuration);
        return services;
    }

    [Fact]
    public void NoRedisEndpoint_SelectsInMemory()
    {
        // Nothing configured → single-instance mode → the in-memory registry, resolvable.
        var services = Wire(new Dictionary<string, string?>());
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IPresenceRegistry>();
        Assert.IsType<InMemoryPresenceRegistry>(registry);

        // No reaper is needed in single-instance mode.
        Assert.DoesNotContain(
            services,
            d => d.ServiceType == typeof(IHostedService)
                && d.ImplementationType == typeof(PresenceReaperService));
    }

    [Fact]
    public void RedisEndpointSet_SelectsRedis_AndRegistersReaper()
    {
        // Endpoint set → the Redis-backed registry is wired, and its heartbeat/reaper hosted
        // service is registered. Inspect descriptors only — never build/connect.
        var services = Wire(new Dictionary<string, string?>
        {
            [RedisBackplaneOptions.EndpointEnvVar] = "localhost:6379",
            [RedisBackplaneOptions.SslEnvVar] = "false",
        });

        // The Redis registry is registered and IPresenceRegistry is NOT the in-memory type.
        Assert.Contains(services, d => d.ServiceType == typeof(RedisPresenceRegistry));

        var presenceDescriptor = Assert.Single(
            services, d => d.ServiceType == typeof(IPresenceRegistry));
        Assert.NotEqual(typeof(InMemoryPresenceRegistry), presenceDescriptor.ImplementationType);

        Assert.Contains(
            services,
            d => d.ServiceType == typeof(IHostedService)
                && d.ImplementationType == typeof(PresenceReaperService));
    }

    [Fact]
    public void CoalescerFlushService_AlwaysRegistered()
    {
        // The coalescer flush loop runs in both modes (a no-op when nothing opted in).
        var services = Wire(new Dictionary<string, string?>());
        Assert.Contains(
            services,
            d => d.ServiceType == typeof(IHostedService)
                && d.ImplementationType == typeof(PresenceCoalescerService));
    }
}
