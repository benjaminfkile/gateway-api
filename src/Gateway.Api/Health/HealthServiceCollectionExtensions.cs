using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gateway.Api.Health;

/// <summary>
/// Registration for the aggregated health check (tech-spec §4.1). Wires a named
/// <see cref="HttpClient"/> for probing, the default HTTP prober, and the
/// aggregator. <c>TryAdd</c> is used for <see cref="IHealthProber"/> so tests can
/// register a fake prober before calling this.
/// </summary>
public static class HealthServiceCollectionExtensions
{
    public static IServiceCollection AddAggregatedHealth(this IServiceCollection services)
    {
        // A dedicated client for probes. The 3s bound is also enforced per-probe
        // via a linked CancellationTokenSource in HttpHealthProber; setting it
        // here too keeps the socket from lingering past the deadline.
        services.AddHttpClient(HttpHealthProber.HttpClientName, client =>
        {
            client.Timeout = HttpHealthProber.ProbeTimeout;
        });

        // TryAdd so a test can substitute a fake prober by registering it first.
        services.TryAddSingleton<IHealthProber, HttpHealthProber>();

        // Scoped: the aggregator depends on IManifestStore, which is scoped when
        // backed by EF Core.
        services.AddScoped<HealthAggregator>();
        return services;
    }
}
