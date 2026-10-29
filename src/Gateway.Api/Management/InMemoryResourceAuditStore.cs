using Gateway.Api.Data;

namespace Gateway.Api.Management;

/// <summary>
/// <see cref="IResourceAuditStore"/> for a gateway with no database: the newest
/// <see cref="Capacity"/> rows in process memory, lost on restart. Registered as a
/// singleton so the fleet refresh watcher still finds the refresh a request started.
/// </summary>
public sealed class InMemoryResourceAuditStore : IResourceAuditStore
{
    /// <summary>Rows kept before the oldest is dropped.</summary>
    public const int Capacity = 500;

    private readonly object _gate = new();
    private readonly LinkedList<ResourceAudit> _rows = new();
    private long _nextId = 1;

    public Task<ResourceAudit> AddAsync(ResourceAudit row, CancellationToken ct = default)
    {
        lock (_gate)
        {
            row.Id = _nextId++;
            _rows.AddFirst(row);
            if (_rows.Count > Capacity)
            {
                _rows.RemoveLast();
            }

            return Task.FromResult(row);
        }
    }

    public Task<IReadOnlyList<ResourceAudit>> ListAsync(int limit, string? resource = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<ResourceAudit> rows = _rows
                .Where(r => resource is null || string.Equals(r.Resource, resource, StringComparison.Ordinal))
                .Take(limit)
                .ToList();
            return Task.FromResult(rows);
        }
    }
}
