using Gateway.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Instances;

/// <summary>
/// <see cref="IInstanceStatusStore"/> over <see cref="GatewayDbContext"/> — the
/// production implementation reading/writing the Postgres <c>instance_status</c>
/// table (tech-spec §4.4). Registered scoped, alongside the scoped DbContext.
/// </summary>
public sealed class EfInstanceStatusStore : IInstanceStatusStore
{
    private readonly GatewayDbContext _db;

    public EfInstanceStatusStore(GatewayDbContext db)
    {
        _db = db;
    }

    public async Task UpsertAsync(InstanceStatus status, CancellationToken ct = default)
    {
        var existing = await _db.InstanceStatus
            .FirstOrDefaultAsync(x => x.InstanceId == status.InstanceId, ct);

        if (existing is null)
        {
            _db.InstanceStatus.Add(status);
        }
        else
        {
            _db.Entry(existing).CurrentValues.SetValues(status);
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<InstanceStatus>> GetAllAsync(CancellationToken ct = default)
    {
        return await _db.InstanceStatus.AsNoTracking().ToListAsync(ct);
    }

    public async Task<int> DeleteStaleAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        // instance_status holds one row per live instance (bounded by fleet size),
        // so materialising the table and filtering in memory is cheap — and it keeps
        // the DateTimeOffset comparison portable across providers (Sqlite in tests
        // cannot translate a DateTimeOffset comparison to SQL; Postgres can).
        var all = await _db.InstanceStatus.ToListAsync(ct);
        var stale = all.Where(x => x.HeartbeatAt < cutoff).ToList();

        if (stale.Count == 0)
        {
            return 0;
        }

        _db.InstanceStatus.RemoveRange(stale);
        await _db.SaveChangesAsync(ct);
        return stale.Count;
    }
}
