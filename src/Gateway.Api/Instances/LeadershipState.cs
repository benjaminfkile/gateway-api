namespace Gateway.Api.Instances;

/// <summary>
/// The cached result of the reconciler's most recent leader-election evaluation
/// (tech-spec §4.3, task #217). A singleton the reconcile loop writes on every
/// iteration and the <c>GET /internal/leader</c> endpoint reads without ever
/// touching the database or triggering an election — the endpoint's only source
/// of truth is whatever the last loop cached here.
/// <para>
/// Before the first reconcile loop completes the snapshot is <b>never evaluated</b>
/// (<see cref="LeadershipSnapshot.IsLeader"/> = false, both
/// <see cref="LeadershipSnapshot.LeaderInstanceId"/> and
/// <see cref="LeadershipSnapshot.EvaluatedAt"/> null). A booting instance must not
/// claim leadership through this endpoint until the reconciler has actually run.
/// </para>
/// </summary>
public sealed class LeadershipState
{
    private readonly object _gate = new();
    private LeadershipSnapshot _snapshot = LeadershipSnapshot.NeverEvaluated;

    /// <summary>
    /// Publish the latest evaluation. Called by the reconcile loop right after
    /// <see cref="ILeaderElection.TryAcquireAsync"/> resolves, on both the success and
    /// the exception path (the exception path runs this instance as non-leader with a
    /// null leader id, so the endpoint reports the loss immediately).
    /// </summary>
    public void Update(bool isLeader, string? leaderInstanceId, DateTimeOffset evaluatedAt)
    {
        lock (_gate)
        {
            _snapshot = new LeadershipSnapshot(isLeader, leaderInstanceId, evaluatedAt);
        }
    }

    /// <summary>The last evaluation the reconciler published, or the never-evaluated seed.</summary>
    public LeadershipSnapshot Snapshot()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }
}

/// <summary>
/// A single leadership evaluation snapshot exposed by <c>GET /internal/leader</c>
/// (task #217). All three fields are read together and never partially updated.
/// </summary>
/// <param name="IsLeader">
/// Whether this instance was the leader as of <paramref name="EvaluatedAt"/>. False
/// before the reconciler has run for the first time.
/// </param>
/// <param name="LeaderInstanceId">
/// The id the last evaluation resolved as leader, or null when unknown (before the
/// first reconcile loop, or when the election could not name a leader this loop).
/// </param>
/// <param name="EvaluatedAt">
/// When the reconciler derived this snapshot. Null before the first evaluation, so
/// downstream clients can tell "not yet known" apart from "known false".
/// </param>
public readonly record struct LeadershipSnapshot(
    bool IsLeader, string? LeaderInstanceId, DateTimeOffset? EvaluatedAt)
{
    /// <summary>The seed used before the reconciler has ever evaluated leadership.</summary>
    public static readonly LeadershipSnapshot NeverEvaluated = new(false, null, null);
}
