namespace Gateway.Api.Instances;

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
    /// Report whether this instance is the leader <b>right now</b>. Called once per
    /// reconcile loop so leadership is continuously re-derived — when the current
    /// leader dies its heartbeat goes stale and another instance takes over on its
    /// next loop, bounded by the stale threshold.
    /// </summary>
    Task<bool> TryAcquireAsync(CancellationToken ct = default);
}
