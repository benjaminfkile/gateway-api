using Gateway.Api.Instances;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Api.Tests;

/// <summary>
/// Unit tests for the lease-fenced Postgres advisory-lock leader election
/// (tech-spec §4.3). All Postgres SQL is behind <see cref="ILeaderLockGateway"/>,
/// so a shared in-memory <see cref="FakeCluster"/> models the advisory lock + lease
/// + backend fencing — the build box has no Postgres. Covers: a healthy leader is
/// never fenced, a stale lease is fenced → acquired → lease upserted, two followers
/// racing to fence yield exactly one leader, and graceful release clears the lease.
/// </summary>
public class LeaderElectionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(90);

    private static PostgresAdvisoryLockLeaderElection Election(
        FakeCluster cluster, string instanceId, MutableClock clock, out FakeLeaderLockGateway gateway)
    {
        gateway = new FakeLeaderLockGateway(cluster);
        return new PostgresAdvisoryLockLeaderElection(
            gateway,
            _ => Task.FromResult(instanceId),
            Threshold,
            NullLogger.Instance,
            clock);
    }

    [Fact]
    public async Task FreshAcquire_TakesLock_AndUpsertsLease()
    {
        var cluster = new FakeCluster();
        var clock = new MutableClock(T0);
        var election = Election(cluster, "i-1", clock, out var gw);

        Assert.True(await election.TryAcquireAsync());

        Assert.Equal(gw.Pid, cluster.LockHolderPid);
        Assert.NotNull(cluster.Lease);
        Assert.Equal("i-1", cluster.Lease!.HolderInstanceId);
        Assert.Equal(T0, cluster.Lease.RenewedAt);
        Assert.Equal(0, cluster.FenceCalls);
    }

    [Fact]
    public async Task HeldLeader_RenewsLease_EachLoop_WithoutRelocking()
    {
        var cluster = new FakeCluster();
        var clock = new MutableClock(T0);
        var election = Election(cluster, "i-1", clock, out _);

        Assert.True(await election.TryAcquireAsync());
        var acquiredAt = cluster.Lease!.AcquiredAt;

        clock.Now = T0 + TimeSpan.FromSeconds(30);
        Assert.True(await election.TryAcquireAsync());

        // Still leader; lease renewed but acquired_at unchanged, and the lock was
        // never re-taken (a session-lock must not stack).
        Assert.Equal(T0 + TimeSpan.FromSeconds(30), cluster.Lease!.RenewedAt);
        Assert.Equal(acquiredAt, cluster.Lease.AcquiredAt);
        Assert.Equal(1, cluster.LockAcquireAttempts);
    }

    [Fact]
    public async Task HealthyLeader_IsNeverFenced_ByFollower()
    {
        var cluster = new FakeCluster();
        var clock = new MutableClock(T0);

        var leader = Election(cluster, "i-leader", clock, out _);
        Assert.True(await leader.TryAcquireAsync());

        // The follower runs a bit later, but the leader keeps renewing so the lease
        // is fresh — the follower must stay a follower and must not fence anyone.
        clock.Now = T0 + TimeSpan.FromSeconds(30);
        Assert.True(await leader.TryAcquireAsync()); // leader renews
        var follower = Election(cluster, "i-follower", clock, out _);

        Assert.False(await follower.TryAcquireAsync());
        Assert.Equal(0, cluster.FenceCalls);
        Assert.Equal("i-leader", cluster.Lease!.HolderInstanceId);
    }

    [Fact]
    public async Task SlowLeader_WithinThreshold_IsNotFenced()
    {
        var cluster = new FakeCluster();
        var clock = new MutableClock(T0);
        var leader = Election(cluster, "i-leader", clock, out _);
        Assert.True(await leader.TryAcquireAsync());

        // Lease last renewed 89s ago — under the 90s threshold. Merely slow, not dead.
        clock.Now = T0 + TimeSpan.FromSeconds(89);
        var follower = Election(cluster, "i-follower", clock, out _);

        Assert.False(await follower.TryAcquireAsync());
        Assert.Equal(0, cluster.FenceCalls);
    }

    [Fact]
    public async Task StaleLease_Fences_Acquires_AndUpsertsOwnLease()
    {
        var cluster = new FakeCluster();
        var clock = new MutableClock(T0);

        // A hard-killed leader: its session still pins the lock, and its lease is
        // stale (last renewed 5 minutes ago).
        cluster.LockHolderPid = 999;
        cluster.Lease = new LeaseSnapshot("i-dead", T0 - TimeSpan.FromMinutes(10), T0 - TimeSpan.FromMinutes(5));

        var follower = Election(cluster, "i-new", clock, out var gw);

        Assert.True(await follower.TryAcquireAsync());

        Assert.True(cluster.FencedPids.Contains(999), "the dead holder's backend was terminated");
        Assert.Equal(gw.Pid, cluster.LockHolderPid);
        Assert.Equal("i-new", cluster.Lease!.HolderInstanceId);
        Assert.Equal(T0, cluster.Lease.RenewedAt);
        Assert.Equal(T0, cluster.Lease.AcquiredAt);
    }

    [Fact]
    public async Task StaleLease_ButNothingToFence_StillReacquiresIfLockFreed()
    {
        var cluster = new FakeCluster();
        var clock = new MutableClock(T0);

        // Lease is stale but the lock is already free (holder's connection dropped on
        // its own). Fencing finds nothing, but the retry still wins the lock.
        cluster.LockHolderPid = null;
        cluster.Lease = new LeaseSnapshot("i-dead", T0 - TimeSpan.FromMinutes(10), T0 - TimeSpan.FromMinutes(5));

        var follower = Election(cluster, "i-new", clock, out var gw);

        Assert.True(await follower.TryAcquireAsync());
        Assert.Equal(gw.Pid, cluster.LockHolderPid);
        Assert.Equal("i-new", cluster.Lease!.HolderInstanceId);
    }

    [Fact]
    public async Task NullLease_IsNotFenced_EvenWhenLockHeld()
    {
        var cluster = new FakeCluster();
        var clock = new MutableClock(T0);

        // Lock held but no lease row yet (a brand-new leader mid-acquisition, or a
        // pre-lease binary). Guard rail: no positive proof of death → do not fence.
        cluster.LockHolderPid = 777;
        cluster.Lease = null;

        var follower = Election(cluster, "i-new", clock, out _);

        Assert.False(await follower.TryAcquireAsync());
        Assert.Equal(0, cluster.FenceCalls);
        Assert.Equal(777, cluster.LockHolderPid);
    }

    [Fact]
    public async Task TwoFollowersRacingToFence_ExactlyOneWins()
    {
        var cluster = new FakeCluster();
        var clock = new MutableClock(T0);

        cluster.LockHolderPid = 999; // the dead leader's pinned session
        cluster.Lease = new LeaseSnapshot("i-dead", T0 - TimeSpan.FromMinutes(10), T0 - TimeSpan.FromMinutes(5));

        // Both followers observe the (stale) lease before either fences — the real
        // race. Each first lease read is gated; releasing A first (and letting it win)
        // then B exercises the guard rail: B re-reads before fencing, sees A's now-fresh
        // lease, and backs off instead of fencing the fresh leader. Only one wins.
        var gateA = new TaskCompletionSource();
        var gateB = new TaskCompletionSource();
        var a = Election(cluster, "i-a", clock, out var gwA);
        var b = Election(cluster, "i-b", clock, out var gwB);
        gwA.FirstLeaseReadGate = gateA;
        gwB.FirstLeaseReadGate = gateB;

        var ta = a.TryAcquireAsync();
        var tb = b.TryAcquireAsync(); // both now suspended having seen the stale lease

        gateA.SetResult();
        Assert.True(await ta); // A fences 999, acquires, publishes a fresh lease

        gateB.SetResult();
        Assert.False(await tb); // B re-reads, sees the fresh lease, backs off

        Assert.Equal(gwA.Pid, cluster.LockHolderPid);
        Assert.Equal(1, cluster.FenceCalls); // only the winner fenced
        Assert.Contains(999, cluster.FencedPids);
        Assert.Equal("i-a", cluster.Lease!.HolderInstanceId);
    }

    [Fact]
    public async Task GracefulDispose_ClearsOwnLease()
    {
        var cluster = new FakeCluster();
        var clock = new MutableClock(T0);
        var election = Election(cluster, "i-1", clock, out _);

        Assert.True(await election.TryAcquireAsync());
        Assert.NotNull(cluster.Lease);

        await election.DisposeAsync();

        Assert.Null(cluster.Lease);       // lease cleared for a fast successor
        Assert.Null(cluster.LockHolderPid); // lock connection released
    }

    [Fact]
    public async Task LockConnectionDeath_ReContends_NextLoop()
    {
        var cluster = new FakeCluster();
        var clock = new MutableClock(T0);
        var election = Election(cluster, "i-1", clock, out var gw);

        Assert.True(await election.TryAcquireAsync());

        // Simulate the dedicated lock connection dropping (Postgres releases the lock).
        cluster.KillConnection(gw.Pid!.Value);

        // Next loop: detects the dead connection, re-contends, and reacquires (the
        // lock is free), landing on a fresh backend.
        Assert.True(await election.TryAcquireAsync());
        Assert.Equal(gw.Pid, cluster.LockHolderPid);
        Assert.NotEqual(999, cluster.LockHolderPid);
    }

    // ----- fakes ------------------------------------------------------------

    /// <summary>Manually-advanced clock so tests control lease staleness deterministically.</summary>
    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now;
        public MutableClock(DateTimeOffset now) => Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// In-memory stand-in for the shared Postgres server: one advisory lock (held by
    /// a backend pid), the singleton lease row, and a set of terminated backends.
    /// Multiple <see cref="FakeLeaderLockGateway"/> instances contend on one cluster.
    /// </summary>
    private sealed class FakeCluster
    {
        public readonly object Sync = new();
        public int NextPid = 1000;
        public int? LockHolderPid;
        public LeaseSnapshot? Lease;
        public readonly HashSet<int> DeadPids = new();
        public readonly HashSet<int> FencedPids = new();
        public int FenceCalls;
        public int LockAcquireAttempts;

        public void KillConnection(int pid)
        {
            lock (Sync)
            {
                DeadPids.Add(pid);
                if (LockHolderPid == pid)
                {
                    LockHolderPid = null;
                }
            }
        }
    }

    private sealed class FakeLeaderLockGateway : ILeaderLockGateway
    {
        private readonly FakeCluster _c;
        public int? Pid { get; private set; }
        public TaskCompletionSource? FirstLeaseReadGate;
        private bool _firstLeaseRead = true;

        public FakeLeaderLockGateway(FakeCluster cluster) => _c = cluster;

        public Task<bool> TryAcquireLockAsync(CancellationToken ct)
        {
            lock (_c.Sync)
            {
                _c.LockAcquireAttempts++;
                Pid ??= _c.NextPid++;
                if (_c.LockHolderPid is null)
                {
                    _c.LockHolderPid = Pid;
                    return Task.FromResult(true);
                }

                return Task.FromResult(false);
            }
        }

        public Task<bool> IsLockConnectionAliveAsync(CancellationToken ct)
        {
            lock (_c.Sync)
            {
                var alive = Pid is int p && !_c.DeadPids.Contains(p) && _c.LockHolderPid == p;
                return Task.FromResult(alive);
            }
        }

        public Task ReleaseLockConnectionAsync()
        {
            lock (_c.Sync)
            {
                if (Pid is int p && _c.LockHolderPid == p)
                {
                    _c.LockHolderPid = null;
                }

                Pid = null;
                return Task.CompletedTask;
            }
        }

        public async Task<LeaseSnapshot?> GetLeaseAsync(CancellationToken ct)
        {
            LeaseSnapshot? snapshot;
            lock (_c.Sync)
            {
                snapshot = _c.Lease;
            }

            // First read may be gated so two racing followers both observe the stale
            // lease before either one fences.
            if (_firstLeaseRead)
            {
                _firstLeaseRead = false;
                if (FirstLeaseReadGate is not null)
                {
                    await FirstLeaseReadGate.Task;
                }
            }

            return snapshot;
        }

        public Task UpsertLeaseAsync(string instanceId, DateTimeOffset now, CancellationToken ct)
        {
            lock (_c.Sync)
            {
                var acquiredAt = _c.Lease is { } l && l.HolderInstanceId == instanceId ? l.AcquiredAt : now;
                _c.Lease = new LeaseSnapshot(instanceId, acquiredAt, now);
                return Task.CompletedTask;
            }
        }

        public Task RenewLeaseAsync(string instanceId, DateTimeOffset now, CancellationToken ct)
        {
            lock (_c.Sync)
            {
                if (_c.Lease is { } l && l.HolderInstanceId == instanceId)
                {
                    _c.Lease = l with { RenewedAt = now };
                }

                return Task.CompletedTask;
            }
        }

        public Task ClearLeaseAsync(string instanceId, CancellationToken ct)
        {
            lock (_c.Sync)
            {
                if (_c.Lease is { } l && l.HolderInstanceId == instanceId)
                {
                    _c.Lease = null;
                }

                return Task.CompletedTask;
            }
        }

        public Task<int?> FenceLockHolderAsync(CancellationToken ct)
        {
            lock (_c.Sync)
            {
                _c.FenceCalls++;
                if (_c.LockHolderPid is int holder)
                {
                    _c.DeadPids.Add(holder);
                    _c.FencedPids.Add(holder);
                    _c.LockHolderPid = null; // termination frees the lock
                    return Task.FromResult<int?>(holder);
                }

                return Task.FromResult<int?>(null);
            }
        }

        public ValueTask DisposeAsync() => new(ReleaseLockConnectionAsync());
    }
}
