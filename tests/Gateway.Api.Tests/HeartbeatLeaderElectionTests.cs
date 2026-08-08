using Gateway.Api.Data;
using Gateway.Api.Instances;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Api.Tests;

/// <summary>
/// Unit tests for <see cref="HeartbeatLeaderElection"/> (tech-spec §4.3): the leader
/// is the live instance with the lowest <c>instance_id</c> among the
/// <c>instance_status</c> rows heartbeating within the stale threshold — no lock, no
/// lease, no session state. Driven entirely against <see cref="FakeInstanceStatusStore"/>
/// (the build box has no Postgres). Covers: a lone instance leads, lowest-id wins,
/// hard-death recovery bounded by the threshold, stale rows are never counted, and a
/// booting instance sees itself after its first heartbeat.
/// </summary>
public class HeartbeatLeaderElectionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(90);

    private static HeartbeatLeaderElection Election(
        FakeInstanceStatusStore store, string instanceId, MutableClock clock, TimeSpan? threshold = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInstanceStatusStore>(store);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var metadata = new InstanceMetadataProvider(
            new IInstanceMetadata[] { new StubInstanceMetadata(new InstanceIdentity(instanceId, null, null)) });

        return new HeartbeatLeaderElection(
            scopeFactory, metadata, threshold ?? Threshold, NullLogger<HeartbeatLeaderElection>.Instance, clock);
    }

    private static InstanceStatus Row(string id, DateTimeOffset heartbeat) =>
        new() { InstanceId = id, HeartbeatAt = heartbeat };

    [Fact]
    public async Task SingleInstance_IsLeader()
    {
        var store = new FakeInstanceStatusStore();
        var clock = new MutableClock(T0);
        var election = Election(store, "i-1", clock);

        Assert.True(await election.TryAcquireAsync());
    }

    [Fact]
    public async Task LowestId_WinsAmongFreshRows()
    {
        var store = new FakeInstanceStatusStore();
        var clock = new MutableClock(T0);
        // A fresh peer with the lowest id is already present.
        store.Seed(Row("i-1", T0));

        // i-2 evaluates: it upserts its own fresh heartbeat, but i-1 is the lowest
        // live id, so i-2 is a follower.
        Assert.False(await Election(store, "i-2", clock).TryAcquireAsync());

        // i-1 evaluates against the same cluster and leads.
        Assert.True(await Election(store, "i-1", clock).TryAcquireAsync());
    }

    [Fact]
    public async Task StaleRows_AreNeverCounted()
    {
        var store = new FakeInstanceStatusStore();
        var clock = new MutableClock(T0);
        // A departed instance with the lowest id, but its heartbeat aged out.
        store.Seed(Row("i-0", T0 - TimeSpan.FromMinutes(5)));

        // i-1 is the only live instance, so it leads despite i-0's lower id.
        Assert.True(await Election(store, "i-1", clock).TryAcquireAsync());
    }

    [Fact]
    public async Task HardDeath_Recovery_NextInstanceTakesOver_BoundedByThreshold()
    {
        var store = new FakeInstanceStatusStore();
        var clock = new MutableClock(T0);
        // i-1 is the current leader, heartbeating at T0.
        store.Seed(Row("i-1", T0));

        // While i-1 is alive, i-2 is a follower.
        Assert.False(await Election(store, "i-2", clock).TryAcquireAsync());

        // i-1 is hard-killed: its row stays at T0 and never refreshes. Just past the
        // stale threshold, i-1 drops out of the candidate set and i-2 takes over on
        // its next evaluation — recovery is bounded by the threshold, with no zombie
        // session pinning leadership.
        clock.Now = T0 + Threshold + TimeSpan.FromSeconds(1);
        Assert.True(await Election(store, "i-2", clock).TryAcquireAsync());
    }

    [Fact]
    public async Task BootingInstance_SeesItself_AfterFirstHeartbeat()
    {
        var store = new FakeInstanceStatusStore();
        var clock = new MutableClock(T0);
        var election = Election(store, "i-boot", clock);

        // No row exists yet for this instance.
        Assert.False(store.Rows.ContainsKey("i-boot"));

        // The election upserts its own heartbeat FIRST, then evaluates from a fresh
        // read — so a booting instance sees itself and can take leadership.
        Assert.True(await election.TryAcquireAsync());

        Assert.True(store.Rows.ContainsKey("i-boot"));
        Assert.Equal(T0, store.Rows["i-boot"].HeartbeatAt);
    }

    [Fact]
    public async Task NoDbMode_InMemoryElection_Unchanged()
    {
        Assert.True(await new InMemoryLeaderElection(isLeader: true).TryAcquireAsync());
        Assert.False(await new InMemoryLeaderElection(isLeader: false).TryAcquireAsync());
    }

    /// <summary>Manually-advanced clock so tests control heartbeat staleness deterministically.</summary>
    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now;
        public MutableClock(DateTimeOffset now) => Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
