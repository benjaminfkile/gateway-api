using Gateway.Api.Data;

namespace Gateway.Api.Management;

/// <summary>
/// Read/write access to <c>resource_audit</c> (docs/load.md sections 3 and 8): one row
/// each time the load scheduler's orphan guard sets the generator group to 0, beside
/// the older rows the table keeps from earlier releases. Two implementations:
/// <see cref="EfResourceAuditStore"/> over the gateway database and
/// <see cref="InMemoryResourceAuditStore"/> for a box with no database.
/// </summary>
public interface IResourceAuditStore
{
    /// <summary>Insert a row; returns it with its id.</summary>
    Task<ResourceAudit> AddAsync(ResourceAudit row, CancellationToken ct = default);

    /// <summary>
    /// The newest <paramref name="limit"/> rows, newest first, optionally only those of
    /// <paramref name="resource"/>.
    /// </summary>
    Task<IReadOnlyList<ResourceAudit>> ListAsync(int limit, string? resource = null, CancellationToken ct = default);
}
