using Gateway.Api.Resources;

namespace Gateway.Api.Tests;

/// <summary>
/// In-memory <see cref="ICacheScaler"/> for tests: replication groups a test fills in,
/// every call recorded; a modify sets the node type and the status to <c>modifying</c>.
/// </summary>
public sealed class FakeCacheScaler : ICacheScaler
{
    private readonly object _gate = new();

    public Dictionary<string, CacheReplicationGroup> Groups { get; } = new(StringComparer.Ordinal);
    public List<string> Calls { get; } = new();

    public Task<CacheReplicationGroup?> DescribeReplicationGroupAsync(string id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Calls.Add($"describe {id}");
            return Task.FromResult(Groups.TryGetValue(id, out var group) ? group : null);
        }
    }

    public Task ModifyNodeTypeAsync(string id, string nodeType, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Calls.Add($"modify {id} {nodeType}");
            Groups[id] = new CacheReplicationGroup(id, nodeType, "modifying");
            return Task.CompletedTask;
        }
    }
}
