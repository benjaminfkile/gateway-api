using Microsoft.Extensions.DependencyInjection.Extensions;
using Yarp.ReverseProxy.Configuration;

namespace Gateway.Api.Proxy;

/// <summary>
/// Registration for the manifest-driven YARP proxy (tech-spec §4.1). Shared by
/// the application host and integration tests so both wire the proxy identically.
/// A manifest store (<see cref="Manifest.IManifestStore"/>) must be registered
/// separately.
/// </summary>
public static class ProxyServiceCollectionExtensions
{
    public static IServiceCollection AddManifestProxy(this IServiceCollection services)
    {
        // TryAdd so a host/test can supply its own resolver (e.g. pointing routes
        // at a localhost test server) by registering it first.
        services.TryAddSingleton<IServiceAddressResolver, HostLoopbackAddressResolver>();
        // Container-truth port map, shared with the reconciler and health prober so
        // routes forward to the port each container is actually bound to.
        services.TryAddSingleton<ServiceHostPortMap>();
        services.AddSingleton<ManifestProxyConfigProvider>();
        services.AddSingleton<IProxyConfigProvider>(sp =>
            sp.GetRequiredService<ManifestProxyConfigProvider>());
        services.AddSingleton<ProxyStateService>();
        // Load the initial route table on startup (runs under the test host too,
        // unlike code after WebApplication.Build()).
        services.AddHostedService<ProxyRouteInitializer>();
        services.AddReverseProxy();
        return services;
    }
}
