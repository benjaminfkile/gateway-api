using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Api.Systemd;

/// <summary>
/// Registration for the systemd watchdog self-check (tech-spec §6). The hosted
/// service is always added but no-ops unless the process is running under systemd
/// with the watchdog enabled, so it is inert in tests and local runs.
/// </summary>
public static class SystemdServiceCollectionExtensions
{
    public static IServiceCollection AddSystemdWatchdog(this IServiceCollection services)
    {
        services.AddHostedService<SystemdWatchdogService>();
        return services;
    }
}
