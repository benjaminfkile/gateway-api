namespace Gateway.Api.Instances;

/// <summary>
/// A snapshot of the singleton <c>leader_lease</c> row (tech-spec §4.3).
/// </summary>
/// <param name="HolderInstanceId">Instance id of the current lease holder.</param>
/// <param name="AcquiredAt">When the holder first acquired the lease.</param>
/// <param name="RenewedAt">When the holder last renewed the lease — the liveness signal.</param>
internal sealed record LeaseSnapshot(string HolderInstanceId, DateTimeOffset AcquiredAt, DateTimeOffset RenewedAt);

/// <summary>
/// The small Postgres-specific seam behind <see cref="PostgresAdvisoryLockLeaderElection"/>
/// (tech-spec §4.3). It isolates every bit of pg-specific SQL — the session
/// advisory lock, the <c>leader_lease</c> row, and <c>pg_terminate_backend</c>
/// fencing — so the election algorithm (when to fence, race handling, guard rails)
/// is unit-tested against a fake, without a real database. The build/test box has
/// no Postgres.
/// <para>
/// The advisory lock is <b>session-scoped</b>: it lives on a single dedicated
/// connection this gateway keeps open, and Postgres frees it when that connection
/// closes. Lease reads/writes and fencing use their own short-lived connections so
/// they never disturb the lock-holding session.
/// </para>
/// </summary>
internal interface ILeaderLockGateway : IAsyncDisposable
{
    /// <summary>
    /// Ensure the dedicated lock connection is open and attempt
    /// <c>pg_try_advisory_lock</c> on the fleet key. Returns whether this session
    /// now holds the lock. Must not be called on a session that already holds it
    /// (the lock stacks); the caller tracks held-state and re-verifies via
    /// <see cref="IsLockConnectionAliveAsync"/> instead.
    /// </summary>
    Task<bool> TryAcquireLockAsync(CancellationToken ct);

    /// <summary>
    /// Cheap liveness ping on the dedicated lock connection. A dead connection means
    /// Postgres already released the lock and leadership is lost.
    /// </summary>
    Task<bool> IsLockConnectionAliveAsync(CancellationToken ct);

    /// <summary>
    /// Tear down the dedicated lock connection, which releases the session advisory
    /// lock server-side. Idempotent.
    /// </summary>
    Task ReleaseLockConnectionAsync();

    /// <summary>Read the singleton lease row, or <c>null</c> if none exists.</summary>
    Task<LeaseSnapshot?> GetLeaseAsync(CancellationToken ct);

    /// <summary>
    /// Upsert the singleton lease to <paramref name="instanceId"/>: sets
    /// <c>acquired_at</c> when the holder changes (fresh acquisition), always sets
    /// <c>renewed_at</c> = <paramref name="now"/>. Called on (re)acquiring leadership.
    /// </summary>
    Task UpsertLeaseAsync(string instanceId, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Renew <c>renewed_at</c> = <paramref name="now"/> for the current holder,
    /// only if <paramref name="instanceId"/> still holds the lease. Called each loop
    /// while leadership is held.
    /// </summary>
    Task RenewLeaseAsync(string instanceId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Delete the singleton lease if held by <paramref name="instanceId"/> (graceful release).</summary>
    Task ClearLeaseAsync(string instanceId, CancellationToken ct);

    /// <summary>
    /// Find the backend holding the fleet advisory lock via <c>pg_locks</c> /
    /// <c>pg_stat_activity</c> and <c>pg_terminate_backend</c> it, freeing the lock a
    /// dead leader's still-open session is pinning. Runs on its own connection.
    /// Returns the terminated backend pid, or <c>null</c> when no holder was found
    /// (e.g. another follower already fenced it).
    /// </summary>
    Task<int?> FenceLockHolderAsync(CancellationToken ct);
}
