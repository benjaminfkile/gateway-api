namespace Gateway.Api.Load;

/// <summary>The database's connection ceiling and the connections in use.</summary>
/// <param name="MaxConnections">The server's <c>max_connections</c> setting.</param>
/// <param name="InUse">Rows of <c>pg_stat_activity</c>.</param>
public sealed record DatabaseConnections(int MaxConnections, int InUse);

/// <summary>
/// Reads the gateway database's connection headroom for the <c>database_headroom</c>
/// pre-flight check (docs/load.md section 6). Behind an interface so the build/test
/// box, which has no Postgres, substitutes a fake.
/// </summary>
public interface IDatabaseHeadroom
{
    /// <summary>The connection numbers, or null when there is no database to ask.</summary>
    Task<DatabaseConnections?> GetAsync(CancellationToken ct = default);
}

/// <summary>The <see cref="IDatabaseHeadroom"/> of a gateway with no database: always null.</summary>
public sealed class NullDatabaseHeadroom : IDatabaseHeadroom
{
    public Task<DatabaseConnections?> GetAsync(CancellationToken ct = default) =>
        Task.FromResult<DatabaseConnections?>(null);
}
