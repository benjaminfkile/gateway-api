namespace Gateway.Api.Instances;

/// <summary>
/// The outcome of a single leader-election evaluation (tech-spec §4.3): whether
/// <b>this</b> instance is the leader as of the evaluation, and the id the evaluation
/// resolved as leader (which may be this instance, another live instance, or null when
/// the implementation cannot name it — e.g. the in-memory single-node stub). Kept as
/// a record struct so it is allocation-free on the reconcile hot path.
/// </summary>
/// <param name="IsLeader">True when this instance is the leader as of the evaluation.</param>
/// <param name="LeaderInstanceId">
/// The id the evaluation resolved as leader, or null when unknown. When this equals
/// the caller's own id, <see cref="IsLeader"/> is true.
/// </param>
public readonly record struct LeaderResolution(bool IsLeader, string? LeaderInstanceId);

/// <summary>
/// Fleet leader election (tech-spec §4.3): with more than one instance, every box
/// converges its own containers independently, but the leader additionally performs
/// <b>fleet-wide</b> duties (e.g. pruning stale <c>instance_status</c> rows, marking
/// deploys complete). Those duties are idempotent, so a brief dual-leader overlap
/// during a transition is harmless. The production implementation is
/// <see cref="HeartbeatLeaderElection"/> (lowest live <c>instance_id</c> derived from
/// heartbeats — no lock, no lease); tests and single-node no-DB dev use
/// <see cref="InMemoryLeaderElection"/>.
/// </summary>
public interface ILeaderElection
{
    /// <summary>
    /// Report whether this instance is the leader <b>right now</b>, along with the
    /// resolved leader id when the implementation knows it. Called once per reconcile
    /// loop so leadership is continuously re-derived — when the current leader dies its
    /// heartbeat goes stale and another instance takes over on its next loop, bounded
    /// by the stale threshold.
    /// </summary>
    Task<LeaderResolution> TryAcquireAsync(CancellationToken ct = default);
}
