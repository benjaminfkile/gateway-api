using Amazon.ElastiCache;
using Amazon.ElastiCache.Model;

namespace Gateway.Api.Resources;

/// <summary>
/// <see cref="ICacheScaler"/> over Amazon ElastiCache (AWSSDK.ElastiCache). The client
/// comes from the container: default credential chain, region from the environment.
/// An unknown replication group reads as null; every other AWS error is thrown.
/// </summary>
public sealed class ElastiCacheScaler : ICacheScaler
{
    private readonly IAmazonElastiCache _elastiCache;

    public ElastiCacheScaler(IAmazonElastiCache elastiCache)
    {
        _elastiCache = elastiCache;
    }

    public async Task<CacheReplicationGroup?> DescribeReplicationGroupAsync(string id, CancellationToken ct = default)
    {
        try
        {
            var response = await _elastiCache.DescribeReplicationGroupsAsync(new DescribeReplicationGroupsRequest
            {
                ReplicationGroupId = id,
            }, ct);
            var group = response.ReplicationGroups?.FirstOrDefault();
            return group is null ? null : new CacheReplicationGroup(group.ReplicationGroupId, group.CacheNodeType, group.Status);
        }
        catch (ReplicationGroupNotFoundException)
        {
            return null;
        }
    }

    public Task ModifyNodeTypeAsync(string id, string nodeType, CancellationToken ct = default) =>
        _elastiCache.ModifyReplicationGroupAsync(new ModifyReplicationGroupRequest
        {
            ReplicationGroupId = id,
            CacheNodeType = nodeType,
            ApplyImmediately = true,
        }, ct);
}
