using Gateway.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Load;

/// <summary>
/// <see cref="IDatabaseHeadroom"/> over the gateway's own database connection: one
/// query for <c>max_connections</c> from <c>pg_settings</c> and one count of
/// <c>pg_stat_activity</c>. Registered scoped beside the DbContext. Any provider other
/// than Postgres answers null.
/// </summary>
public sealed class EfDatabaseHeadroom : IDatabaseHeadroom
{
    private readonly GatewayDbContext _db;

    public EfDatabaseHeadroom(GatewayDbContext db)
    {
        _db = db;
    }

    public async Task<DatabaseConnections?> GetAsync(CancellationToken ct = default)
    {
        if (!_db.Database.IsNpgsql())
        {
            return null;
        }

        var max = await _db.Database
            .SqlQueryRaw<int>("select setting::int as \"Value\" from pg_settings where name = 'max_connections'")
            .SingleAsync(ct);
        var inUse = await _db.Database
            .SqlQueryRaw<long>("select count(*) as \"Value\" from pg_stat_activity")
            .SingleAsync(ct);
        return new DatabaseConnections(max, (int)inUse);
    }
}
