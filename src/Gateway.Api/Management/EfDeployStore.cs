using Gateway.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Management;

/// <summary>
/// <see cref="IDeployStore"/> over <see cref="GatewayDbContext"/> — the production
/// implementation reading/writing the Postgres <c>deploy_history</c> and
/// <c>deploy_instance_status</c> tables (tech-spec §4.4). Registered scoped,
/// alongside the scoped DbContext.
/// </summary>
public sealed class EfDeployStore : IDeployStore
{
    private readonly GatewayDbContext _db;

    public EfDeployStore(GatewayDbContext db)
    {
        _db = db;
    }

    public async Task<DeployHistory> AddAsync(DeployHistory record, CancellationToken ct = default)
    {
        _db.DeployHistory.Add(record);
        await _db.SaveChangesAsync(ct);
        return record;
    }

    public async Task UpdateAsync(DeployHistory record, CancellationToken ct = default)
    {
        var existing = await _db.DeployHistory.FirstOrDefaultAsync(x => x.Id == record.Id, ct);
        if (existing is null)
        {
            return;
        }

        _db.Entry(existing).CurrentValues.SetValues(record);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<DeployHistory>> ListAsync(CancellationToken ct = default)
    {
        return await _db.DeployHistory.AsNoTracking()
            .OrderByDescending(x => x.Id)
            .ToListAsync(ct);
    }

    public async Task<DeployHistory?> GetAsync(int id, CancellationToken ct = default)
    {
        return await _db.DeployHistory.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<IReadOnlyList<DeployHistory>> ListForServiceAsync(string service, CancellationToken ct = default)
    {
        return await _db.DeployHistory.AsNoTracking()
            .Where(x => x.Service == service)
            .OrderByDescending(x => x.Id)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DeployHistory>> ListInProgressAsync(CancellationToken ct = default)
    {
        return await _db.DeployHistory.AsNoTracking()
            .Where(x => x.Status == DeployStatus.InProgress)
            .OrderBy(x => x.Id)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DeployInstanceStatus>> ListInstanceStatusesAsync(int deployId, CancellationToken ct = default)
    {
        return await _db.DeployInstanceStatus.AsNoTracking()
            .Where(x => x.DeployId == deployId)
            .ToListAsync(ct);
    }

    public async Task UpsertInstanceStatusAsync(DeployInstanceStatus status, CancellationToken ct = default)
    {
        var existing = await _db.DeployInstanceStatus
            .FirstOrDefaultAsync(x => x.DeployId == status.DeployId && x.InstanceId == status.InstanceId, ct);

        if (existing is null)
        {
            _db.DeployInstanceStatus.Add(status);
        }
        else
        {
            _db.Entry(existing).CurrentValues.SetValues(status);
        }

        await _db.SaveChangesAsync(ct);
    }
}
