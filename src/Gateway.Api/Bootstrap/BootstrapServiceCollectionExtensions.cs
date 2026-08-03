using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gateway.Api.Bootstrap;

/// <summary>
/// Registration for the node-bootstrap pipeline (tech-spec §4.3). Binds
/// <see cref="BootstrapOptions"/> from the <c>Bootstrap</c> configuration section,
/// gates enablement on <c>GATEWAY_BOOTSTRAP_ENABLED</c>, and wires the ordered
/// steps behind the <see cref="ILinuxHost"/> seam plus the run-once hosted service
/// and the registry-credential refresh timer.
/// <para>
/// The image-registry dependency of the registry-login step comes from
/// <c>AddManagementServices</c>. <c>TryAdd</c> is used for the host and pipeline so
/// tests can substitute fakes; the steps are added with <c>AddSingleton</c> so all
/// four are resolved as <see cref="IBootstrapStep"/> in registration order.
/// </para>
/// </summary>
public static class BootstrapServiceCollectionExtensions
{
    public static IServiceCollection AddNodeBootstrap(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = new BootstrapOptions();
        configuration.GetSection("Bootstrap").Bind(options);

        // The env flag is authoritative for enablement, overriding any config value.
        var flag = Environment.GetEnvironmentVariable(BootstrapOptions.EnabledEnvVar)
            ?? configuration[BootstrapOptions.EnabledEnvVar];
        options.Enabled = string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);

        services.TryAddSingleton(options);
        services.TryAddSingleton<ILinuxHost, LinuxHost>();

        // Ordered pipeline: daemon config → internal network → registry login →
        // metrics-agent config. Registration order is the execution order.
        services.AddSingleton<IBootstrapStep, DockerDaemonConfigStep>();
        services.AddSingleton<IBootstrapStep, DockerNetworkStep>();
        services.AddSingleton<RegistryLoginStep>();
        services.AddSingleton<IBootstrapStep>(sp => sp.GetRequiredService<RegistryLoginStep>());
        services.AddSingleton<IBootstrapStep, CloudWatchAgentConfigStep>();

        services.TryAddSingleton<INodeBootstrap, NodeBootstrap>();

        services.AddHostedService<BootstrapHostedService>();
        services.AddHostedService<RegistryCredentialRefreshService>();

        return services;
    }
}
