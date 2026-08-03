namespace Gateway.Api.Instances;

/// <summary>
/// Fleet leader election (tech-spec §4.3): with more than one instance, every box
/// converges its own containers independently, but exactly one — the leader —
/// performs <b>fleet-wide</b> duties (e.g. pruning stale <c>instance_status</c>
/// rows, marking deploys complete). The production implementation is
/// <see cref="PostgresAdvisoryLockLeaderElection"/> (a Postgres advisory lock);
/// tests and single-node dev use <see cref="InMemoryLeaderElection"/>.
/// </summary>
public interface ILeaderElection
{
    /// <summary>
    /// Acquire leadership if it is free, or re-confirm it if already held, and
    /// report whether this instance is the leader <b>right now</b>. Called once per
    /// reconcile loop so leadership is continuously re-verified — if the previous
    /// leader dies its lock is released and another instance takes over on its next
    /// loop.
    /// </summary>
    Task<bool> TryAcquireAsync(CancellationToken ct = default);
}
