using Gateway.Api.Management;

namespace Gateway.Api.Tests;

/// <summary>
/// In-memory <see cref="ILogGroupAdmin"/> for tests: the build box has no
/// AWS/CloudWatch access. Records every retention call so a test can assert it was
/// issued once per service group.
/// </summary>
public sealed class FakeLogGroupAdmin : ILogGroupAdmin
{
    private readonly object _gate = new();

    /// <summary>Every retention call received: (group, retentionDays), in order.</summary>
    public List<(string Group, int Days)> Calls { get; } = new();

    public Task EnsureRetentionAsync(string logGroup, int retentionDays, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Calls.Add((logGroup, retentionDays));
        }

        return Task.CompletedTask;
    }
}
