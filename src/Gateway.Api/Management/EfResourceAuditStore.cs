using Gateway.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Management;

/// <summary><see cref="IResourceAuditStore"/> over the gateway database. Registered scoped beside the DbContext.</summary>
public sealed class EfResourceAuditStore : IResourceAuditStore
{
    private readonly GatewayDbContext _db;

    public EfResourceAuditStore(GatewayDbContext db)
    {
        _db = db;
    }

    public async Task<ResourceAudit> AddAsync(ResourceAudit row, CancellationToken ct = default)
    {
        _db.ResourceAudit.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<IReadOnlyList<ResourceAudit>> ListAsync(int limit, string? resource = null, CancellationToken ct = default)
    {
        var query = _db.ResourceAudit.AsNoTracking();
        if (resource is not null)
        {
            query = query.Where(r => r.Resource == resource);
        }

        return await query.OrderByDescending(r => r.Id).Take(limit).ToListAsync(ct);
    }
}
