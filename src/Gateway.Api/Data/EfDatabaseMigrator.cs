using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Gateway.Api.Data;

/// <summary>
/// Applies the gateway's pending EF Core migrations, serialized fleet-wide with a
/// Postgres advisory lock (tech-spec §6, §7; same mechanism as
/// <see cref="Instances.PostgresAdvisoryLockLeaderElection"/> on a <b>distinct</b>
/// key). On boot several ASG instances may race to migrate a fresh database; the
/// lock guarantees exactly one runs <c>Database.Migrate</c> while the rest block,
/// then find nothing pending and proceed.
/// <para>
/// A single <see cref="MigrateAsync"/> call is one attempt and throws if the
/// database is unreachable or a migration fails; the surrounding retry/backoff and
/// fail-fast policy lives in <see cref="DatabaseMigrationHostedService"/>. The
/// build/test box has no Postgres, so this type is only wired when a connection
/// string is configured; tests drive the policy through a fake
/// <see cref="IDatabaseMigrator"/>.
/// </para>
/// </summary>
public sealed class EfDatabaseMigrator : IDatabaseMigrator
{
    /// <summary>
    /// Advisory-lock key identifying "the gateway is migrating the schema".
    /// Deliberately distinct from <see cref="Instances.PostgresAdvisoryLockLeaderElection.DefaultLockKey"/>
    /// so migration serialization and leader election never contend on the same lock.
    /// </summary>
    public const long MigrationLockKey = 0x6761_7465_6d69_67L; // "gatemig" in hex-ish

    private readonly string _connectionString;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EfDatabaseMigrator>? _logger;

    public EfDatabaseMigrator(
        string connectionString,
        IServiceScopeFactory scopeFactory,
        ILogger<EfDatabaseMigrator>? logger = null)
    {
        _connectionString = connectionString;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        // Serialize on a dedicated connection: pg_advisory_lock is session-scoped,
        // so holding it for the duration of the migration blocks every other
        // instance until we release it (or our connection drops). This is a
        // blocking lock — unlike leader election's try-lock — because followers
        // must wait for the migrator to finish, not skip ahead.
        await using var lockConnection = new NpgsqlConnection(_connectionString);
        await lockConnection.OpenAsync(ct);

        await ExecuteAsync(lockConnection, "SELECT pg_advisory_lock(@key)", ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pending.Count == 0)
            {
                _logger?.LogInformation("Database schema is up to date; no migrations to apply.");
                return;
            }

            _logger?.LogInformation(
                "Applying {Count} pending EF Core migration(s): {Migrations}",
                pending.Count, string.Join(", ", pending));
            await db.Database.MigrateAsync(ct);
            _logger?.LogInformation("Database migrations applied successfully.");
        }
        finally
        {
            // Best-effort unlock; closing the connection releases session locks
            // regardless, so a failure here is not fatal.
            try
            {
                await ExecuteAsync(lockConnection, "SELECT pg_advisory_unlock(@key)", CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to release migration advisory lock; connection close will free it.");
            }
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("key", MigrationLockKey);
        await cmd.ExecuteScalarAsync(ct);
    }
}
