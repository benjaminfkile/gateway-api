namespace Gateway.Api.Data;

/// <summary>
/// Singleton leader-liveness lease (tech-spec §4.3). The fleet leader holds a
/// Postgres session advisory lock as its primary mutex, but a hard-killed leader
/// (EC2 terminate, kernel panic) never closes its TCP connection, so Postgres can
/// keep that session — and the lock — alive for as long as TCP keepalives take
/// (potentially hours). This lease row is the <b>liveness truth</b>: the leader
/// renews <see cref="RenewedAt"/> every reconcile loop, so a stale lease reveals a
/// dead holder and lets a follower fence it (see
/// <see cref="Instances.PostgresAdvisoryLockLeaderElection"/>).
/// <para>
/// Exactly one row ever exists, keyed by the fixed <see cref="SingletonId"/>.
/// </para>
/// </summary>
public class LeaderLease
{
    /// <summary>The one and only lease row id; the table is a singleton.</summary>
    public const int SingletonId = 1;

    /// <summary>Fixed primary key (always <see cref="SingletonId"/>).</summary>
    public int Id { get; set; } = SingletonId;

    /// <summary>Instance id of the current lease holder (the leader).</summary>
    public string HolderInstanceId { get; set; } = default!;

    /// <summary>When the current holder first acquired the lease.</summary>
    public DateTimeOffset AcquiredAt { get; set; }

    /// <summary>
    /// When the current holder last renewed the lease. A value older than the
    /// configured stale threshold marks the holder dead and eligible for fencing.
    /// </summary>
    public DateTimeOffset RenewedAt { get; set; }
}
