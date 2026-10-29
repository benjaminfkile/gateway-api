using Gateway.Api.Resources;

namespace Gateway.Api.Tests;

/// <summary>
/// In-memory <see cref="IDatabaseScaler"/> for tests: instances a test fills in, every
/// call recorded; a modify sets the class and the status to <c>modifying</c>.
/// </summary>
public sealed class FakeDatabaseScaler : IDatabaseScaler
{
    private readonly object _gate = new();

    public Dictionary<string, DatabaseInstanceState> Instances { get; } = new(StringComparer.Ordinal);
    public List<string> Calls { get; } = new();

    public Task<DatabaseInstanceState?> DescribeInstanceAsync(string id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Calls.Add($"describe {id}");
            return Task.FromResult(Instances.TryGetValue(id, out var instance) ? instance : null);
        }
    }

    public Task ModifyInstanceClassAsync(string id, string instanceClass, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Calls.Add($"modify {id} {instanceClass}");
            if (Instances.TryGetValue(id, out var instance))
            {
                Instances[id] = instance with { InstanceClass = instanceClass, Status = "modifying" };
            }

            return Task.CompletedTask;
        }
    }
}
