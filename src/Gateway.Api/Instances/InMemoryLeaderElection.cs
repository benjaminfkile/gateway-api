namespace Gateway.Api.Instances;

/// <summary>
/// In-process <see cref="ILeaderElection"/> for tests and single-node dev, where
/// there is no fleet to coordinate. Defaults to being the leader (a lone instance
/// must run the leader-only duties), but <see cref="IsLeader"/> is settable so
/// tests can exercise both the leader and non-leader paths.
/// </summary>
public sealed class InMemoryLeaderElection : ILeaderElection
{
    public InMemoryLeaderElection(bool isLeader = true)
    {
        IsLeader = isLeader;
    }

    /// <summary>Whether this instance reports as the leader. Settable for tests.</summary>
    public bool IsLeader { get; set; }

    public Task<bool> TryAcquireAsync(CancellationToken ct = default) => Task.FromResult(IsLeader);
}
