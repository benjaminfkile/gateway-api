using Microsoft.Extensions.Logging;

namespace Gateway.Api.Instances;

/// <summary>
/// Lease-fenced Postgres advisory-lock leader election (tech-spec §3, §4.3).
/// Leadership is a session-level <c>pg_try_advisory_lock</c> on a fixed key — the
/// primary mutex — held for as long as a <b>dedicated</b> connection stays open.
/// That alone is not enough: when a leader is <b>hard-killed</b> (EC2 terminate,
/// kernel panic) its TCP connection is never closed, so Postgres keeps the session
/// — and the lock — alive until TCP keepalives expire (potentially hours). Every
/// follower's <c>pg_try_advisory_lock</c> then returns false indefinitely and
/// leader-only duties silently stop.
/// <para>
/// The fix adds a <b>lease</b> (<see cref="Data.LeaderLease"/>) as the liveness
/// truth: the leader renews it every reconcile loop. A follower that cannot get the
/// lock reads the lease — if it is <b>stale</b> (renewed_at older than
/// <see cref="_staleThreshold"/>) the holder is dead, so the follower <b>fences</b>
/// it (<c>pg_terminate_backend</c> on the backend pinning the lock) and retries the
/// lock once. Only one racing follower wins the retry. A merely-slow but live leader
/// keeps renewing, so its lease never goes stale and it is never fenced.
/// </para>
/// <para>
/// All Postgres-specific SQL lives behind <see cref="ILeaderLockGateway"/> so this
/// algorithm is unit-tested against a fake; the build/test box has no Postgres.
/// </para>
/// </summary>
public sealed class PostgresAdvisoryLockLeaderElection : ILeaderElection, IAsyncDisposable
{
    /// <summary>
    /// Fixed advisory-lock key identifying "the gateway fleet leader". Arbitrary but
    /// stable — every instance in the fleet contends on this same key.
    /// </summary>
    public const long DefaultLockKey = 0x6761_7465_7761_79L; // "gateway" in hex-ish

    /// <summary>
    /// Configuration key (a <see cref="TimeSpan"/>) for how old the lease may get
    /// before a follower treats the holder as dead and fences it. Defaults to
    /// <see cref="DefaultStaleThreshold"/> — the same 90s as the instance-status
    /// stale threshold (tech-spec §4.4).
    /// </summary>
    public const string StaleThresholdConfigKey = "LeaderElection:LeaseStaleThreshold";

    /// <summary>Default lease staleness threshold (tech-spec §4.4: 90s).</summary>
    public static readonly TimeSpan DefaultStaleThreshold = TimeSpan.FromSeconds(90);

    private readonly ILeaderLockGateway _gateway;
    private readonly Func<CancellationToken, Task<string>> _instanceIdProvider;
    private readonly TimeSpan _staleThreshold;
    private readonly TimeProvider _clock;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _instanceId;
    private bool _held;

    /// <summary>Production constructor: builds the Npgsql-backed gateway from a connection string.</summary>
    public PostgresAdvisoryLockLeaderElection(
        string connectionString,
        Func<CancellationToken, Task<string>> instanceIdProvider,
        TimeSpan? staleThreshold = null,
        long lockKey = DefaultLockKey,
        ILogger<PostgresAdvisoryLockLeaderElection>? logger = null)
        : this(
            new NpgsqlLeaderLockGateway(connectionString, lockKey, logger),
            instanceIdProvider,
            staleThreshold ?? DefaultStaleThreshold,
            logger)
    {
    }

    /// <summary>Test/seam constructor: inject a fake <see cref="ILeaderLockGateway"/> and clock.</summary>
    internal PostgresAdvisoryLockLeaderElection(
        ILeaderLockGateway gateway,
        Func<CancellationToken, Task<string>> instanceIdProvider,
        TimeSpan staleThreshold,
        ILogger? logger = null,
        TimeProvider? clock = null)
    {
        _gateway = gateway;
        _instanceIdProvider = instanceIdProvider;
        _staleThreshold = staleThreshold;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<bool> TryAcquireAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var instanceId = await ResolveInstanceIdAsync(ct);

            // 1. Already leader? Re-verify the dedicated lock connection is alive
            //    (a dropped connection means Postgres released the lock) and renew
            //    the lease so followers see us as live. We must NOT re-run
            //    pg_try_advisory_lock on a session that already holds it.
            if (_held)
            {
                if (await _gateway.IsLockConnectionAliveAsync(ct))
                {
                    await RenewLeaseAsync(instanceId, ct);
                    return true;
                }

                _logger?.LogWarning("Lost fleet-leader lock connection; will re-contend.");
                _held = false;
                await _gateway.ReleaseLockConnectionAsync();
            }

            // 2. Contend for the lock.
            if (await _gateway.TryAcquireLockAsync(ct))
            {
                return await BecomeLeaderAsync(instanceId, fencedPid: null, ct);
            }

            // 3. Lock is held by someone else. Fence only if the lease proves the
            //    holder is dead — never a merely-slow leader (tech-spec §4.3 guard rail).
            var lease = await _gateway.GetLeaseAsync(ct);
            if (!IsLeaseStale(lease, out var age))
            {
                return false;
            }

            // Re-read immediately before fencing: a racing follower may have just
            // fenced-and-acquired and refreshed the lease in the meantime. Backing off
            // on a now-fresh lease keeps us from terminating a healthy new leader
            // (tech-spec §4.3 guard rail) and means only one racer actually fences.
            lease = await _gateway.GetLeaseAsync(ct);
            if (!IsLeaseStale(lease, out age))
            {
                _logger?.LogInformation(
                    "Fleet leader lease refreshed before fencing; a peer won the race. Running as follower.");
                return false;
            }

            _logger?.LogWarning(
                "Fleet leader appears dead: instance {Me} found lease held by {Holder} last renewed {Age} ago " +
                "(threshold {Threshold}). Fencing the stale holder.",
                instanceId, lease?.HolderInstanceId ?? "(none)", age, _staleThreshold);

            var fencedPid = await _gateway.FenceLockHolderAsync(ct);
            if (fencedPid is int pid)
            {
                _logger?.LogWarning(
                    "Instance {Me} fenced dead leader {Holder} (terminated backend pid {Pid}).",
                    instanceId, lease?.HolderInstanceId ?? "(none)", pid);
            }

            // 4. Retry the lock once. Two followers can fence concurrently; only one
            //    wins this retry — the other stays a follower this loop.
            if (await _gateway.TryAcquireLockAsync(ct))
            {
                return await BecomeLeaderAsync(instanceId, fencedPid, ct);
            }

            _logger?.LogInformation(
                "Instance {Me} lost the post-fence race for fleet leadership; running as follower.", instanceId);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Any DB error means we cannot claim leadership this loop; fail closed
            // (not leader) and reset so the next loop reconnects cleanly.
            _logger?.LogWarning(ex, "Leader-election attempt failed; treating as non-leader.");
            _held = false;
            await SafeReleaseLockConnectionAsync();
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> BecomeLeaderAsync(string instanceId, int? fencedPid, CancellationToken ct)
    {
        _held = true;
        // On acquiring leadership (fresh or after fencing), publish the lease
        // immediately so followers treat us as the live leader (tech-spec §4.3).
        await _gateway.UpsertLeaseAsync(instanceId, _clock.GetUtcNow(), ct);

        if (fencedPid is int pid)
        {
            _logger?.LogWarning(
                "Instance {Me} acquired fleet leadership after fencing the dead holder (pid {Pid}).",
                instanceId, pid);
        }
        else
        {
            _logger?.LogInformation("Instance {Me} acquired fleet-leader advisory lock.", instanceId);
        }

        return true;
    }

    private async Task RenewLeaseAsync(string instanceId, CancellationToken ct)
    {
        // The lease is liveness bookkeeping; a write hiccup must not cost us the
        // advisory lock (the primary mutex), so renew best-effort.
        try
        {
            await _gateway.RenewLeaseAsync(instanceId, _clock.GetUtcNow(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to renew leader lease; still holding the advisory lock.");
        }
    }

    private bool IsLeaseStale(LeaseSnapshot? lease, out TimeSpan age)
    {
        // No lease row => a brand-new leader mid-acquisition (or a pre-lease binary);
        // do not fence without positive proof of staleness (tech-spec §4.3 guard rail).
        if (lease is null)
        {
            age = TimeSpan.Zero;
            return false;
        }

        age = _clock.GetUtcNow() - lease.RenewedAt;
        return age > _staleThreshold;
    }

    private async Task<string> ResolveInstanceIdAsync(CancellationToken ct)
    {
        return _instanceId ??= await _instanceIdProvider(ct);
    }

    private async Task SafeReleaseLockConnectionAsync()
    {
        try
        {
            await _gateway.ReleaseLockConnectionAsync();
        }
        catch
        {
            // Best-effort; the next loop reconnects.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            // Graceful release: clear our lease so a successor need not wait out the
            // stale threshold, then drop the lock connection (frees the lock).
            if (_held && _instanceId is not null)
            {
                try
                {
                    await _gateway.ClearLeaseAsync(_instanceId, CancellationToken.None);
                }
                catch
                {
                    // Best-effort; a leftover lease just ages out and gets fenced.
                }
            }

            _held = false;
            await _gateway.DisposeAsync();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
