namespace Gateway.Api.Resources;

/// <summary>A replication group as <see cref="ICacheScaler.DescribeReplicationGroupAsync"/> reads it.</summary>
/// <param name="Id">The replication group id.</param>
/// <param name="NodeType">The group's cache node type, e.g. <c>cache.t4g.small</c>.</param>
/// <param name="Status">The ElastiCache status, e.g. <c>available</c> or <c>modifying</c>.</param>
public sealed record CacheReplicationGroup(string Id, string? NodeType, string? Status);

/// <summary>
/// The ElastiCache operations the Resources page needs for the realtime backplane
/// (docs/load.md section 9). The gateway resizes the replication group that exists; it
/// never creates or deletes one.
/// <para>
/// Behind an interface so the build/test box, which has no AWS access, substitutes a
/// fake. <see cref="ElastiCacheScaler"/> is registered when
/// <c>Resources:CacheReplicationGroup</c> is set, <see cref="NullCacheScaler"/>
/// otherwise.
/// </para>
/// </summary>
public interface ICacheScaler
{
    /// <summary>The replication group, or null when no such group exists.</summary>
    Task<CacheReplicationGroup?> DescribeReplicationGroupAsync(string id, CancellationToken ct = default);

    /// <summary>Change the replication group's node type, applied immediately.</summary>
    Task ModifyNodeTypeAsync(string id, string nodeType, CancellationToken ct = default);
}
