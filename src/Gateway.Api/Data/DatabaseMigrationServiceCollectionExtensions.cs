using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Data;

/// <summary>
/// Wires automatic schema migration on boot (tech-spec §6). Call this only when a
/// database is configured: it registers the pending <see cref="MigrationReadinessGate"/>,
/// the Postgres-backed <see cref="EfDatabaseMigrator"/>, and the hosted service
/// that applies migrations (with retry/backoff + fail-fast) before the reconciler
/// starts. When no database is configured this is never called, the gate defaults
/// to <see cref="MigrationReadinessGate.AlreadyReady"/> (registered by the
/// reconciler), and boot proceeds with zero database traffic.
/// </summary>
public static class DatabaseMigrationServiceCollectionExtensions
{
    public static IServiceCollection AddDatabaseMigration(
        this IServiceCollection services,
        string connectionString,
        IConfiguration configuration)
    {
        var options = new MigrationOptions();
        configuration.GetSection("Migration").Bind(options);
        services.TryAddSingleton(options);

        // A pending gate: the reconciler/heartbeat block on it until migration
        // completes. Registered here (before AddNodeReconciler's TryAdd default)
        // so the DB path gets the pending gate, not the already-ready one.
        services.TryAddSingleton(new MigrationReadinessGate());

        services.TryAddSingleton<IDatabaseMigrator>(sp =>
            new EfDatabaseMigrator(
                connectionString,
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetService<ILogger<EfDatabaseMigrator>>()));

        // Registered before the reconciler so its StartAsync (which applies
        // migrations to completion) runs first in the sequential hosted-service
        // startup, and the gate covers any residual ordering concern.
        services.AddHostedService<DatabaseMigrationHostedService>();
        return services;
    }
}
