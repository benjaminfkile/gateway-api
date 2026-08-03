using Gateway.Api.Data;

namespace Gateway.Api.Instances;

/// <summary>
/// Persistence for the fleet <c>instance_status</c> table (tech-spec §4.4). Every
/// reconcile loop upserts this instance's row (heartbeat + per-service inventory);
/// the leader additionally prunes rows whose heartbeat has gone stale. Behind an
/// interface so the reconciler is testable against Sqlite / fakes — the build box
/// has no Postgres.
/// </summary>
public interface IInstanceStatusStore
{
    /// <summary>Insert or update this instance's row (keyed by instance id).</summary>
    Task UpsertAsync(InstanceStatus status, CancellationToken ct = default);

    /// <summary>
    /// Delete rows whose <see cref="InstanceStatus.HeartbeatAt"/> is strictly older
    /// than <paramref name="cutoff"/> (departed instances). Returns the number of
    /// rows removed. Leader-only duty.
    /// </summary>
    Task<int> DeleteStaleAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
