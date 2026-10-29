using Amazon.AutoScaling;
using Amazon.EC2;
using Amazon.ElastiCache;
using Amazon.RDS;
using Gateway.Api.Load;
using Gateway.Api.Management;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gateway.Api.Resources;

/// <summary>
/// Registration for the Resources page (docs/load.md section 9): the options, the fleet
/// group resolver, the three scalers, the audit store default, the resources clock and
/// the fleet refresh watcher. Each scaler is the AWS implementation when its
/// configuration keys are set and the null one otherwise, decided when it is first
/// resolved, so a dev box without AWS boots and a test host's late-bound configuration
/// is honoured. The AWS clients are built lazily from the default credential chain and
/// the environment's region, one per service. <c>TryAdd</c> throughout so
/// <c>Program</c>'s database branch and tests substitute their own.
/// </summary>
public static class ResourcesServiceCollectionExtensions
{
    public static IServiceCollection AddResources(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => ResourcesOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));

        services.TryAddKeyedSingleton(ResourceEndpoints.ClockKey, TimeProvider.System);

        services.TryAddSingleton<IFleetGroupSource, ImdsFleetGroupSource>();
        services.TryAddSingleton(sp => new FleetGroupResolver(
            sp.GetRequiredService<ResourcesOptions>(),
            sp.GetRequiredService<IFleetGroupSource>(),
            sp.GetRequiredKeyedService<TimeProvider>(ResourceEndpoints.ClockKey)));

        services.TryAddSingleton<IAmazonAutoScaling>(_ => new AmazonAutoScalingClient());
        services.TryAddSingleton<IAmazonEC2>(_ => new AmazonEC2Client());
        services.TryAddSingleton<IAmazonElastiCache>(_ => new AmazonElastiCacheClient());
        services.TryAddSingleton<IAmazonRDS>(_ => new AmazonRDSClient());

        // The fleet scaler serves the fleet group and the load generator group, so it
        // is the AWS one when either is configured.
        services.TryAddSingleton<IFleetScaler>(sp =>
            sp.GetRequiredService<ResourcesOptions>().FleetConfigured
            || sp.GetRequiredService<LoadOptions>().IsConfigured
                ? new AwsFleetScaler(sp.GetRequiredService<IAmazonAutoScaling>(), sp.GetRequiredService<IAmazonEC2>())
                : new NullFleetScaler());

        services.TryAddSingleton<ICacheScaler>(sp =>
            sp.GetRequiredService<ResourcesOptions>().CacheConfigured
                ? new ElastiCacheScaler(sp.GetRequiredService<IAmazonElastiCache>())
                : new NullCacheScaler());

        // Scoped: the RDS scaler reads connections in use through the scoped headroom.
        services.TryAddScoped<IDatabaseScaler>(sp =>
            sp.GetRequiredService<ResourcesOptions>().DatabaseConfigured
                ? new RdsDatabaseScaler(
                    sp.GetRequiredService<IAmazonRDS>(),
                    sp.GetRequiredService<IDatabaseHeadroom>(),
                    sp.GetRequiredService<ILogger<RdsDatabaseScaler>>())
                : new NullDatabaseScaler());

        // No database: the newest rows in memory. Program registers the EF store first
        // when a database is configured.
        services.TryAddSingleton<IResourceAuditStore, InMemoryResourceAuditStore>();

        services.AddHostedService<FleetRefreshWatcher>();

        return services;
    }
}
