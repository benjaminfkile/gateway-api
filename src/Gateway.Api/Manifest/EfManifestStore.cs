using Gateway.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Manifest;

/// <summary>
/// Manifest store over <see cref="GatewayDbContext"/> — the production
/// implementation reading from / writing to the Postgres <c>service_manifest</c>
/// table (tech-spec §4.4). Registered scoped, alongside the scoped DbContext.
/// </summary>
public sealed class EfManifestStore : IManifestStore
{
    private readonly GatewayDbContext _db;

    public EfManifestStore(GatewayDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ServiceManifest>> GetAllAsync(CancellationToken ct = default)
    {
        return await _db.ServiceManifests.AsNoTracking().ToListAsync(ct);
    }

    public async Task<ServiceManifest?> GetAsync(string name, CancellationToken ct = default)
    {
        return await _db.ServiceManifests.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name == name, ct);
    }

    public async Task UpsertAsync(ServiceManifest manifest, CancellationToken ct = default)
    {
        var existing = await _db.ServiceManifests
            .FirstOrDefaultAsync(x => x.Name == manifest.Name, ct);

        if (existing is null)
        {
            _db.ServiceManifests.Add(manifest);
        }
        else
        {
            _db.Entry(existing).CurrentValues.SetValues(manifest);
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task SetDesiredStatusAsync(string name, string desiredStatus, CancellationToken ct = default)
    {
        var existing = await _db.ServiceManifests
            .FirstOrDefaultAsync(x => x.Name == name, ct);

        if (existing is null)
        {
            throw new KeyNotFoundException($"No manifest entry for service '{name}'.");
        }

        existing.DesiredStatus = desiredStatus;
        await _db.SaveChangesAsync(ct);
    }
}
