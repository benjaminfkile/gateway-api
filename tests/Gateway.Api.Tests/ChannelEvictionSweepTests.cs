using System.Text.Json;
using Gateway.Api.RealTime;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Api.Tests;

/// <summary>
/// Unit coverage for mid-connection channel eviction (task #613): the ~1 min sweep that removes
/// an admitted PRIVATE-channel member from its SignalR group once its delegated-auth allow
/// lapses — or once the channel's owning service is deleted — and signals <c>channelEvicted</c>
/// to that connection alone so a well-behaved client re-joins with a fresh credential. Driven
/// entirely offline: a real <see cref="InMemoryPresenceRegistry"/> and
/// <see cref="ChannelAuthDecisionCache"/> against a fake clock, a stub ownership resolver, and
/// the <see cref="FakeGatewayHubContext"/> recording group removals and targeted sends.
/// </summary>
public class ChannelEvictionSweepTests
{
    private const string Channel = "svc-a:room";
    private const string AuthPath = "/realtime/auth";
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    private static readonly TimeSpan AllowTtl = TimeSpan.FromMinutes(15);

    private sealed class StubOwnership : IChannelOwnershipResolver
    {
        private readonly Dictionary<string, ChannelOwner> _owners = new(StringComparer.Ordinal);

        public StubOwnership Add(string prefix, string? authPath)
        {
            _owners[prefix] = new ChannelOwner(prefix, "tok", authPath, null, false, 8080);
            return this;
        }

        public Task<ChannelOwner?> ResolveAsync(string prefix, CancellationToken ct = default) =>
            Task.FromResult(_owners.TryGetValue(prefix, out var o) ? o : null);
    }

    private sealed record Harness(
        ChannelEvictionSweep Sweep,
        InMemoryPresenceRegistry Presence,
        ChannelAuthDecisionCache Decisions,
        HubChannelMembership Membership,
        FakeGatewayHubContext Hub,
        ManualTimeProvider Clock);

    private static Harness Build(StubOwnership ownership, ManualTimeProvider clock)
    {
        var presence = new InMemoryPresenceRegistry(clock);
        var decisions = new ChannelAuthDecisionCache(clock, allowTtl: AllowTtl);
        var membership = new HubChannelMembership();
        var hub = new FakeGatewayHubContext();
        var publisher = new ChannelEventPublisher(hub, NullLogger<ChannelEventPublisher>.Instance);
        var coalescer = new PresenceEventCoalescer(
            presence, ownership, publisher, clock, logger: null, window: TimeSpan.FromSeconds(1));
        var sweep = new ChannelEvictionSweep(
            presence, ownership, decisions, hub, membership, coalescer,
            NullLogger<ChannelEvictionSweep>.Instance);
        return new Harness(sweep, presence, decisions, membership, hub, clock);
    }

    /// <summary>Mirror the hub's admit path: cache the allow (private), add presence, record membership.</summary>
    private static async Task AdmitAsync(Harness h, string channel, string connectionId, string? identity, bool privateAllow)
    {
        if (privateAllow)
        {
            h.Decisions.StoreAllow(connectionId, channel, identity);
        }

        await h.Presence.AddAsync(channel, connectionId, identity);
        h.Membership.Join(connectionId, channel, identity);
    }

    private static JsonElement AsJson(object? arg) =>
        JsonSerializer.SerializeToElement(arg, arg!.GetType());

    [Fact]
    public async Task ExpiredAllow_EvictsThatConnectionOnly_AndReJoinReadmits()
    {
        var ownership = new StubOwnership().Add("svc-a", AuthPath);
        var clock = new ManualTimeProvider { Now = T0 };
        var h = Build(ownership, clock);

        // conn-1 admitted at T0; conn-2 admitted 14 min later (a fresh allow).
        await AdmitAsync(h, Channel, "conn-1", "user-1", privateAllow: true);
        clock.Now = T0 + TimeSpan.FromMinutes(14);
        await AdmitAsync(h, Channel, "conn-2", "user-2", privateAllow: true);

        // At T0+16 conn-1's 15-min allow has lapsed; conn-2's (until T0+29) is still live.
        clock.Now = T0 + TimeSpan.FromMinutes(16);
        await h.Sweep.RunAsync();

        // Only conn-1 is removed from the group and only conn-1 is notified.
        var removal = Assert.Single(h.Hub.GroupRemovals);
        Assert.Equal(("conn-1", Channel), removal);

        var notice = Assert.Single(h.Hub.ClientSends);
        Assert.Equal("conn-1", notice.ConnectionId);
        Assert.Equal(IChannelEventPublisher.ChannelEventMethod, notice.Method);
        var env = AsJson(notice.Arg);
        Assert.Equal(Channel, env.GetProperty("channel").GetString());
        Assert.Equal(ChannelEvictionSweep.EvictedEvent, env.GetProperty("event").GetString());
        Assert.Equal(Channel, env.GetProperty("data").GetProperty("channel").GetString());
        Assert.Equal(ChannelEvictionSweep.AuthExpiredReason, env.GetProperty("data").GetProperty("reason").GetString());

        // Presence and membership reflect the eviction: conn-1 gone, conn-2 kept.
        Assert.Equal(1, await h.Presence.CountAsync(Channel));
        Assert.Equal("conn-2", (await h.Presence.ListAsync(Channel)).Single().ConnectionId);
        Assert.False(h.Membership.TryGetIdentity("conn-1", Channel, out _));
        Assert.True(h.Membership.TryGetIdentity("conn-2", Channel, out _));

        // Re-join with a fresh valid credential (the normal JoinPrivateChannel path) readmits;
        // a subsequent sweep leaves it alone.
        await AdmitAsync(h, Channel, "conn-1", "user-1", privateAllow: true);
        h.Hub.GroupRemovals.Clear();
        h.Hub.ClientSends.Clear();
        await h.Sweep.RunAsync();
        Assert.Empty(h.Hub.GroupRemovals);
        Assert.Empty(h.Hub.ClientSends);
        Assert.Equal(2, await h.Presence.CountAsync(Channel));
    }

    [Fact]
    public async Task ServiceDeleted_EvictsEveryMember_WithServiceRemovedReason()
    {
        // No owner registered for "svc-a" → its manifest row is gone. Every member of its
        // channels is evicted within one sweep, regardless of public/private.
        var ownership = new StubOwnership();
        var clock = new ManualTimeProvider { Now = T0 };
        var h = Build(ownership, clock);

        await AdmitAsync(h, Channel, "conn-1", "user-1", privateAllow: true);
        await AdmitAsync(h, Channel, "conn-2", "user-2", privateAllow: true);

        await h.Sweep.RunAsync();

        Assert.Equal(2, h.Hub.GroupRemovals.Count);
        Assert.Equal(2, h.Hub.ClientSends.Count);
        Assert.All(h.Hub.ClientSends, s =>
            Assert.Equal(
                ChannelEvictionSweep.ServiceRemovedReason,
                AsJson(s.Arg).GetProperty("data").GetProperty("reason").GetString()));
        Assert.Equal(0, await h.Presence.CountAsync(Channel));
    }

    [Fact]
    public async Task PublicChannel_NeverEvicted_EvenLongAfterJoin()
    {
        // A live service WITHOUT an auth path: public channels have no auth to expire.
        var ownership = new StubOwnership().Add("svc-a", authPath: null);
        var clock = new ManualTimeProvider { Now = T0 };
        var h = Build(ownership, clock);

        await AdmitAsync(h, Channel, "conn-1", identity: null, privateAllow: false);
        clock.Now = T0 + TimeSpan.FromHours(1);

        await h.Sweep.RunAsync();

        Assert.Empty(h.Hub.GroupRemovals);
        Assert.Empty(h.Hub.ClientSends);
        Assert.Equal(1, await h.Presence.CountAsync(Channel));
    }

    [Fact]
    public async Task OpsChannel_NeverEvicted()
    {
        // ops:* is Cognito-gated, not delegated-auth — the sweep skips it entirely.
        var ownership = new StubOwnership();
        var clock = new ManualTimeProvider { Now = T0 };
        var h = Build(ownership, clock);

        await AdmitAsync(h, "ops:fleet", "conn-1", identity: null, privateAllow: false);
        clock.Now = T0 + TimeSpan.FromHours(1);

        await h.Sweep.RunAsync();

        Assert.Empty(h.Hub.GroupRemovals);
        Assert.Empty(h.Hub.ClientSends);
        Assert.Equal(1, await h.Presence.CountAsync("ops:fleet"));
    }

    [Fact]
    public async Task NothingExpired_IsANoOp()
    {
        var ownership = new StubOwnership().Add("svc-a", AuthPath);
        var clock = new ManualTimeProvider { Now = T0 };
        var h = Build(ownership, clock);

        await AdmitAsync(h, Channel, "conn-1", "user-1", privateAllow: true);

        // Well within the allow window: nothing evicted, nothing signalled.
        clock.Now = T0 + TimeSpan.FromMinutes(5);
        await h.Sweep.RunAsync();

        Assert.Empty(h.Hub.GroupRemovals);
        Assert.Empty(h.Hub.ClientSends);
        Assert.Equal(1, await h.Presence.CountAsync(Channel));
    }

    [Fact]
    public async Task NoMemberships_IsANoOp()
    {
        var ownership = new StubOwnership().Add("svc-a", AuthPath);
        var h = Build(ownership, new ManualTimeProvider { Now = T0 });

        await h.Sweep.RunAsync();

        Assert.Empty(h.Hub.GroupRemovals);
        Assert.Empty(h.Hub.ClientSends);
    }
}
