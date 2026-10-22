using Gateway.Api.Containers;
using Gateway.Api.Data;
using Gateway.Api.Instances;
using Gateway.Api.Management;
using Gateway.Api.RealTime;
using Gateway.Api.Reconcile;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gateway.Api.Jobs;

/// <summary>
/// Registration for the job executor (docs/jobs.md section 3). Binds
/// <see cref="JobSchedulerOptions"/> from the <c>Jobs</c> configuration section and
/// adds <see cref="JobSchedulerService"/>. <c>Program</c> calls this only when a
/// database is configured: without one there is no run ledger to drive. The runtime,
/// env provider, leader status and instance identity come from
/// <c>AddNodeReconciler</c>, so jobs and services share them.
/// </summary>
public static class JobSchedulerServiceCollectionExtensions
{
    public static IServiceCollection AddJobScheduler(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new JobSchedulerOptions();
        configuration.GetSection("Jobs").Bind(options);
        if (options.MaxConcurrent < 1)
        {
            options.MaxConcurrent = 1;
        }

        services.TryAddSingleton(options);
        services.TryAddSingleton<IJobMetrics>(sp => new JobMetrics(
            sp.GetRequiredService<JobSchedulerOptions>(),
            sp.GetRequiredService<ILogger<JobMetrics>>()));

        services.AddHostedService(sp => new JobSchedulerService(
            sp.GetRequiredService<IContainerRuntime>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ISecretEnvProvider>(),
            sp.GetRequiredService<ILeaderStatus>(),
            sp.GetRequiredService<InstanceMetadataProvider>(),
            sp.GetRequiredService<JobSchedulerOptions>(),
            sp.GetRequiredService<ReconcilerOptions>(),
            sp.GetRequiredService<IJobMetrics>(),
            sp.GetRequiredService<ILogger<JobSchedulerService>>(),
            sp.GetKeyedService<TimeProvider>(JobEndpoints.ClockKey),
            sp.GetService<MigrationReadinessGate>(),
            sp.GetService<IChannelEventPublisher>()));

        return services;
    }
}
