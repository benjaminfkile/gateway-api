using Gateway.Api.Containers;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gateway.Api.Reconcile;

/// <summary>
/// Registration for the node reconciler (tech-spec §4.3). Binds
/// <see cref="ReconcilerOptions"/> from the <c>Reconciler</c> configuration
/// section, gates it on <c>GATEWAY_RECONCILER_ENABLED</c>, and wires the
/// container runtime, readiness prober, env provider, and outcome reporter.
/// <para>
/// The real <see cref="DockerContainerRuntime"/> is registered only where Docker
/// is present (<see cref="DockerContainerRuntime.IsSupported"/>); elsewhere an
/// <see cref="UnavailableContainerRuntime"/> stands in so the host boots cleanly.
/// Because the reconciler is off by default, the stub is never actually used.
/// <c>TryAdd</c> is used throughout so tests can substitute fakes.
/// </para>
/// </summary>
public static class ReconcilerServiceCollectionExtensions
{
    public static IServiceCollection AddNodeReconciler(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = new ReconcilerOptions();
        configuration.GetSection("Reconciler").Bind(options);

        // The env flag is authoritative for enablement, overriding any config value.
        var flag = Environment.GetEnvironmentVariable(ReconcilerOptions.EnabledEnvVar)
            ?? configuration[ReconcilerOptions.EnabledEnvVar];
        options.Enabled = string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);

        services.TryAddSingleton(options);

        // Container runtime: real Docker only where the socket exists.
        if (DockerContainerRuntime.IsSupported)
        {
            services.TryAddSingleton<IContainerRuntime>(_ => DockerContainerRuntime.Create());
        }
        else
        {
            services.TryAddSingleton<IContainerRuntime, UnavailableContainerRuntime>();
        }

        services.AddHttpClient(HttpReadinessProber.HttpClientName, client =>
        {
            client.Timeout = options.ReadinessTimeout;
        });

        services.TryAddSingleton<IReadinessProber, HttpReadinessProber>();
        services.TryAddSingleton<IServiceEnvProvider, NullServiceEnvProvider>();
        services.TryAddSingleton<IReconcileReporter, LoggingReconcileReporter>();

        services.AddHostedService<ReconcilerService>();
        return services;
    }
}
