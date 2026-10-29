using Gateway.Api.Management;
using Gateway.Api.Resources;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gateway.Api.Load;

/// <summary>
/// Registration for the load feature's part 1 (docs/load.md sections 6, 8 and 12):
/// the options, the store default, the pre-flight and its checks, the fleet scaler,
/// the database headroom reader, the run-token rate limiter and the load clock.
/// <c>TryAdd</c> throughout so <c>Program</c>'s database branch and tests substitute
/// their own. There is no scheduler here: a run stays queued.
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

        // The only fleet scaler in this build: every call throws NotSupportedException.
        services.TryAddSingleton<IFleetScaler, NullFleetScaler>();

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

        return services;
    }
}
