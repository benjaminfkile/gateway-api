using Gateway.Api.Containers;
using Gateway.Api.Data;
using Gateway.Api.Instances;
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

        // Container runtime: real Docker only where the socket exists. Pulls
        // authenticate against ECR via the registry auth provider — the Docker
        // Engine API takes credentials per request, not from `docker login`.
        services.TryAddSingleton<IRegistryAuthProvider>(sp =>
            new EcrRegistryAuthProvider(() => sp.GetRequiredService<Gateway.Api.Management.IImageRegistry>()));
        if (DockerContainerRuntime.IsSupported)
        {
            services.TryAddSingleton<IContainerRuntime>(sp =>
                DockerContainerRuntime.Create(sp.GetRequiredService<IRegistryAuthProvider>()));
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

        // Container-truth port map (tech-spec §7): the reconciler keeps it current
        // so the proxy/health prober forward to the port each container is actually
        // bound to. TryAdd — AddManifestProxy usually registers it first.
        services.TryAddSingleton<Gateway.Api.Proxy.ServiceHostPortMap>();

        // Instance identity (tech-spec §4.4): IMDSv2 first, environment fallback
        // auto-selected when IMDS is unreachable. The IMDS client sets the
        // link-local base address; Ec2InstanceMetadata bounds each call to 2s.
        services.AddHttpClient(Ec2InstanceMetadata.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(Ec2InstanceMetadata.ImdsBaseAddress);
        });
        services.AddSingleton<IInstanceMetadata>(sp =>
            new Ec2InstanceMetadata(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(Ec2InstanceMetadata.HttpClientName)));
        services.AddSingleton<IInstanceMetadata, EnvInstanceMetadata>();
        services.TryAddSingleton<InstanceMetadataProvider>();

        // Leader election + instance-status persistence. Defaults suit a box with
        // no Postgres (single-node dev / tests); Program.cs registers the
        // Postgres-advisory-lock leader and EF-backed store when a DB is configured.
        services.TryAddSingleton<ILeaderElection>(new InMemoryLeaderElection());
        services.TryAddSingleton<IInstanceStatusStore, NullInstanceStatusStore>();

        // Migration gate (this task, tech-spec §6): the loop waits on this before
        // reading the schema. Program.cs registers a pending gate + the migration
        // hosted service when a DB is configured; without a DB there is nothing to
        // migrate, so default to an already-open gate and boot with zero DB traffic.
        services.TryAddSingleton(_ => MigrationReadinessGate.AlreadyReady());

        services.AddHostedService<ReconcilerService>();
        return services;
    }
}
