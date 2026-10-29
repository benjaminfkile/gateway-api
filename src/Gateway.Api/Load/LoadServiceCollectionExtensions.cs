using Amazon.SecretsManager;
using Gateway.Api.Data;
using Gateway.Api.Instances;
using Gateway.Api.Jobs;
using Gateway.Api.Management;
using Gateway.Api.RealTime;
using Gateway.Api.Reconcile;
using Gateway.Api.Resources;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gateway.Api.Load;

/// <summary>
/// Registration for the load feature (docs/load.md sections 3, 6, 8 and 12).
/// <see cref="AddLoad"/>: the options, the store default, the pre-flight and its
/// checks, the database headroom reader, the run-token rate limiter, the load clock and
/// the metrics. <see cref="AddLoadScheduler"/>: the secret writer and the load
/// scheduler, which <c>Program</c> adds only with a database. The fleet scaler both
/// read through is registered by <c>AddResources</c>. <c>TryAdd</c> throughout so
/// <c>Program</c>'s database branch and tests substitute their own.
/// </summary>
public static class LoadServiceCollectionExtensions
{
    public static IServiceCollection AddLoad(this IServiceCollection services)
    {
        // Resolved lazily from the container's IConfiguration so late-bound test
        // configuration is honoured.
        services.TryAddSingleton(sp => LoadOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));

        // No-op defaults until Program wires the EF-backed implementations for a
        // configured database.
        services.TryAddScoped<ILoadStore, NullLoadStore>();
        services.TryAddScoped<IDatabaseHeadroom, NullDatabaseHeadroom>();

        // Clock for run timestamps, the hard stop and pre-flight freshness. Keyed so a
        // test can fix it without moving the clock the rest of the host reads.
        services.TryAddKeyedSingleton(LoadEndpoints.ClockKey, TimeProvider.System);

        services.TryAddSingleton(_ => new LoadTokenRateLimiter());

        // Pre-flight evaluators, in the order of docs/load.md section 6.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ILoadPreflightCheck, GeneratorGroupCheck>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ILoadPreflightCheck, FleetHealthyCheck>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ILoadPreflightCheck, TargetKnownCheck>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ILoadPreflightCheck, ScenarioValidatedCheck>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ILoadPreflightCheck, PresenceOffCheck>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ILoadPreflightCheck, DatabaseHeadroomCheck>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ILoadPreflightCheck, FleetClassCheck>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ILoadPreflightCheck, TargetConfirmedCheck>());
        services.TryAddScoped<ILoadPreflight, LoadPreflight>();

        // Run and stats-line metrics through the same EMF listener and namespace as
        // the job metrics (Jobs:MetricsEndpoint, Jobs:MetricsNamespace).
        services.TryAddSingleton<ILoadMetrics>(sp =>
        {
            var jobs = new JobSchedulerOptions();
            sp.GetRequiredService<IConfiguration>().GetSection("Jobs").Bind(jobs);
            return new LoadMetrics(jobs.MetricsEndpoint, jobs.MetricsNamespace, sp.GetRequiredService<ILogger<LoadMetrics>>());
        });

        return services;
    }

    /// <summary>
    /// The load scheduler (docs/load.md section 3) and the Secrets Manager writer of the
    /// generator spec secret. Only with a database: without one there is no run ledger
    /// to drive. Leader status, reconciler options and the presence registry come from
    /// <c>AddNodeReconciler</c> and <c>AddGatewayRealtime</c>.
    /// </summary>
    public static IServiceCollection AddLoadScheduler(this IServiceCollection services)
    {
        services.TryAddSingleton<IAmazonSecretsManager>(_ => new AmazonSecretsManagerClient());
        services.TryAddSingleton<ISecretWriter>(sp =>
            new SecretsManagerSecretWriter(() => sp.GetRequiredService<IAmazonSecretsManager>()));

        services.AddHostedService(sp => new LoadSchedulerService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IFleetScaler>(),
            sp.GetRequiredService<ISecretWriter>(),
            sp.GetRequiredService<FleetGroupResolver>(),
            sp.GetRequiredService<ILeaderStatus>(),
            sp.GetRequiredService<LoadOptions>(),
            sp.GetRequiredService<ReconcilerOptions>(),
            sp.GetRequiredService<ILoadMetrics>(),
            sp.GetRequiredService<ILogger<LoadSchedulerService>>(),
            sp.GetKeyedService<TimeProvider>(LoadEndpoints.ClockKey),
            sp.GetService<MigrationReadinessGate>(),
            sp.GetService<IChannelEventPublisher>(),
            sp.GetService<IPresenceRegistry>()));

        return services;
    }
}
