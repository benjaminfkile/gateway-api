namespace Gateway.Api.Data;

/// <summary>
/// Seam over "apply the gateway's own EF Core schema migrations" (tech-spec §6,
/// §11: migrations own the manifest/status schema). A single call performs
/// <b>one</b> attempt — serialize with the fleet, then apply any pending
/// migrations — and throws if the database is unreachable or a migration fails.
/// <para>
/// The retry/backoff, startup ordering, and fail-fast policy live in
/// <see cref="DatabaseMigrationHostedService"/>, which drives this seam; keeping
/// the attempt behind this interface lets that policy be unit-tested against a
/// fake with no real Postgres. The production implementation is
/// <see cref="EfDatabaseMigrator"/>.
/// </para>
/// </summary>
public interface IDatabaseMigrator
{
    /// <summary>
    /// Acquire the fleet-wide migration advisory lock (so exactly one instance
    /// migrates while the rest wait), apply all pending migrations, then release.
    /// Throws on any failure — the caller owns retry/backoff and fail-fast.
    /// </summary>
    Task MigrateAsync(CancellationToken ct = default);
}
