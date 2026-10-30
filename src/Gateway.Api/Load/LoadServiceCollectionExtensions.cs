using Amazon.AutoScaling;
using Amazon.SecretsManager;
using Gateway.Api.Data;
using Gateway.Api.Instances;
using Gateway.Api.Jobs;
using Gateway.Api.Management;
using Gateway.Api.RealTime;
using Gateway.Api.Reconcile;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gateway.Api.Load;

/// <summary>
/// Registration for the load feature (docs/load.md sections 3, 6, 8 and 12).
/// <see cref="AddLoad"/>: the options, the store and audit store defaults, the
/// generator group scaler, the pre-flight and its checks, the run-token rate limiter,
/// the load clock and the metrics. <see cref="AddLoadScheduler"/>: the secret writer
/// and the load scheduler, which <c>Program</c> adds only with a database.
/// <c>TryAdd</c> throughout so <c>Program</c>'s database branch and tests substitute
/// their own.
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

        // No database: the newest audit rows in memory. Program registers the EF store
        // first when a database is configured.
        services.TryAddSingleton<IResourceAuditStore, InMemoryResourceAuditStore>();

        // The generator group scaler: the AWS one when Load:GeneratorGroup is set, the
        // null one otherwise, decided when first resolved so a test host's late-bound
        // configuration is honoured. The Auto Scaling client is built lazily from the
        // default credential chain and the environment's region.
        services.TryAddSingleton<IAmazonAutoScaling>(_ => new AmazonAutoScalingClient());
        services.TryAddSingleton<IGeneratorGroupScaler>(sp =>
            sp.GetRequiredService<LoadOptions>().IsConfigured
                ? new AwsGeneratorGroupScaler(sp.GetRequiredService<IAmazonAutoScaling>())
                : new NullGeneratorGroupScaler());

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
            sp.GetRequiredService<IGeneratorGroupScaler>(),
            sp.GetRequiredService<ISecretWriter>(),
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
