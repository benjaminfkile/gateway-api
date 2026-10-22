namespace Gateway.Api.Instances;

/// <summary>
/// The in-process answer to "is this instance the fleet leader", as the reconcile
/// loop last evaluated it (docs/jobs.md section 3). The reconciler feeds it on every
/// loop from the <see cref="LeaderResolution"/> it already computes; readers
/// (<c>GET /internal/leader</c>, the job scheduler) never call the election
/// themselves and never touch the database for it. Implemented by
/// <see cref="LeadershipState"/>.
/// </summary>
public interface ILeaderStatus
{
    /// <summary>
    /// The last resolution. Not leader with no leader id before the first
    /// evaluation, and after an evaluation that failed.
    /// </summary>
    LeaderResolution Current { get; }

    /// <summary>
    /// When <see cref="Current"/> was evaluated, or null before the first evaluation.
    /// A reader compares it with a stale threshold so a stuck reconcile loop cannot
    /// keep an old leader answer alive.
    /// </summary>
    DateTimeOffset? EvaluatedAt { get; }
}
