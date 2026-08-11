using System.Security.Claims;
using Gateway.Api.RealTime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Api.Tests;

/// <summary>
/// Unit coverage for the delegated-auth decision cache and the hub's disconnect
/// cleanup (task #594, requirements 4 &amp; 5): allows live for the connection's
/// lifetime and carry the service-supplied identity, denials expire after a short TTL,
/// and every decision for a connection is dropped on disconnect.
/// </summary>
public class ChannelAuthDecisionCacheTests
{
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void StoreAllow_Cached_WithinAllowTtl_WithIdentity()
    {
        var clock = new TestClock();
        var cache = new ChannelAuthDecisionCache(
            clock, denialTtl: TimeSpan.FromSeconds(10), allowTtl: TimeSpan.FromMinutes(15));

        cache.StoreAllow("conn-1", "svc-a:room", identity: "user-42");

        // Well past the denial TTL but within the allow window: still admitted, identity kept.
        clock.Now += TimeSpan.FromMinutes(10);
        var decision = cache.TryGet("conn-1", "svc-a:room");
        Assert.NotNull(decision);
        Assert.True(decision!.Value.Allowed);
        Assert.Equal("user-42", decision.Value.Identity);
    }

    [Fact]
    public void StoreAllow_ExpiresAfterAllowTtl_ForcingReAuth()
    {
        // Task #608 finding 2: an allow now has a finite TTL so a revoked credential cannot
        // ride a long-lived connection forever — past the window the next join re-authorizes.
        var clock = new TestClock();
        var cache = new ChannelAuthDecisionCache(
            clock, denialTtl: TimeSpan.FromSeconds(10), allowTtl: TimeSpan.FromMinutes(15));

        cache.StoreAllow("conn-1", "svc-a:room", identity: "user-42");

        // Within the window: cached.
        clock.Now += TimeSpan.FromMinutes(14);
        Assert.NotNull(cache.TryGet("conn-1", "svc-a:room"));

        // Past the window: a miss, so the next join re-consults the owning service.
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Null(cache.TryGet("conn-1", "svc-a:room"));
    }

    [Fact]
    public void StoreDeny_ThenDifferentCredential_IsMiss_SameCredentialCached()
    {
        // Task #608 finding 1: the deny is keyed on the credential, so a retry with a
        // DIFFERENT (now-valid) credential is not short-circuited by the stale deny, while
        // a retry with the SAME rejected credential still is (within the TTL).
        var clock = new TestClock();
        var cache = new ChannelAuthDecisionCache(clock, TimeSpan.FromSeconds(10));

        cache.StoreDeny("conn-1", "svc-a:room", credential: "stale-token");

        // Same credential: served from the deny cache.
        var same = cache.TryGet("conn-1", "svc-a:room", "stale-token");
        Assert.NotNull(same);
        Assert.False(same!.Value.Allowed);

        // A different, now-valid credential: a miss, so the join reaches the owning service.
        Assert.Null(cache.TryGet("conn-1", "svc-a:room", "fresh-token"));
    }

    [Fact]
    public void StoreDeny_ThenAllow_SameChannel_AllowWins()
    {
        // A deny (per-credential) and a later allow (credential-independent) coexist under
        // one channel; the allow admits any credential regardless of the earlier deny.
        var cache = new ChannelAuthDecisionCache();

        cache.StoreDeny("conn-1", "svc-a:room", credential: "bad");
        cache.StoreAllow("conn-1", "svc-a:room", identity: "user-1");

        var decision = cache.TryGet("conn-1", "svc-a:room", "anything");
        Assert.NotNull(decision);
        Assert.True(decision!.Value.Allowed);
    }

    [Fact]
    public void Store_AfterDrop_Discarded_WhileTombstoned()
    {
        // Task #608 finding 2a: a late in-flight callback that stores under a dropped
        // connection id (tombstoned) must NOT resurrect the connection's map.
        var clock = new TestClock();
        var cache = new ChannelAuthDecisionCache(
            clock, tombstoneTtl: TimeSpan.FromSeconds(30));

        cache.Drop("conn-1");

        // A store racing in after the drop is discarded while the tombstone is live.
        cache.StoreAllow("conn-1", "svc-a:room", identity: "user-1");
        Assert.Null(cache.TryGet("conn-1", "svc-a:room"));

        // Past the tombstone window the id is reusable again (SignalR never reuses ids, but
        // the tombstone must not pin memory forever).
        clock.Now += TimeSpan.FromSeconds(31);
        cache.StoreAllow("conn-1", "svc-a:room", identity: "user-2");
        Assert.NotNull(cache.TryGet("conn-1", "svc-a:room"));
    }

    [Fact]
    public void PerConnection_EntryCap_EvictsOldest()
    {
        // Task #608 finding 2b: a client looping distinct channel names cannot grow the
        // per-connection cache unboundedly — past the cap the oldest entry is evicted.
        var clock = new TestClock();
        var cache = new ChannelAuthDecisionCache(clock, allowTtl: TimeSpan.FromHours(1));

        // The first (oldest) entry.
        cache.StoreAllow("conn-1", "svc-a:room-0", identity: "id-0");

        // Fill exactly to the cap with newer entries (advancing the clock so ordering is
        // unambiguous), then one more to trip an eviction.
        for (var i = 1; i <= ChannelAuthDecisionCache.MaxEntriesPerConnection; i++)
        {
            clock.Now += TimeSpan.FromSeconds(1);
            cache.StoreAllow("conn-1", $"svc-a:room-{i}", identity: $"id-{i}");
        }

        // The oldest entry was evicted; the newest remains.
        Assert.Null(cache.TryGet("conn-1", "svc-a:room-0"));
        Assert.NotNull(cache.TryGet(
            "conn-1", $"svc-a:room-{ChannelAuthDecisionCache.MaxEntriesPerConnection}"));
    }

    [Fact]
    public void InFlightCallback_CappedAtOne_PerConnection()
    {
        // Task #608 finding 2: at most one in-flight auth callback per connection.
        var cache = new ChannelAuthDecisionCache();

        Assert.True(cache.TryBeginAuthCallback("conn-1"));
        // A second concurrent begin on the same connection is refused.
        Assert.False(cache.TryBeginAuthCallback("conn-1"));
        // A different connection is unaffected.
        Assert.True(cache.TryBeginAuthCallback("conn-2"));

        // Releasing the slot lets the next callback proceed.
        cache.EndAuthCallback("conn-1");
        Assert.True(cache.TryBeginAuthCallback("conn-1"));
    }

    [Fact]
    public void StoreAllow_NullIdentity_Roundtrips()
    {
        var cache = new ChannelAuthDecisionCache();
        cache.StoreAllow("conn-1", "svc-a:room", identity: null);

        var decision = cache.TryGet("conn-1", "svc-a:room");
        Assert.NotNull(decision);
        Assert.True(decision!.Value.Allowed);
        Assert.Null(decision.Value.Identity);
    }

    [Fact]
    public void StoreDeny_HonouredWithinTtl_ThenExpires()
    {
        var clock = new TestClock();
        var cache = new ChannelAuthDecisionCache(clock, TimeSpan.FromSeconds(10));

        cache.StoreDeny("conn-1", "svc-a:room");

        // Within the window the deny is served from cache (no re-callback).
        var within = cache.TryGet("conn-1", "svc-a:room");
        Assert.NotNull(within);
        Assert.False(within!.Value.Allowed);

        // Past the TTL it is a miss, so the next join re-consults the owning service.
        clock.Now += TimeSpan.FromSeconds(11);
        Assert.Null(cache.TryGet("conn-1", "svc-a:room"));
    }

    [Fact]
    public void TryGet_UnknownPair_IsMiss()
    {
        var cache = new ChannelAuthDecisionCache();
        Assert.Null(cache.TryGet("conn-x", "svc-a:room"));
    }

    [Fact]
    public void Drop_RemovesEveryDecisionForConnection()
    {
        var cache = new ChannelAuthDecisionCache();
        cache.StoreAllow("conn-1", "svc-a:one", "id");
        cache.StoreDeny("conn-1", "svc-a:two");
        cache.StoreAllow("conn-2", "svc-a:one", "id");

        cache.Drop("conn-1");

        Assert.Null(cache.TryGet("conn-1", "svc-a:one"));
        Assert.Null(cache.TryGet("conn-1", "svc-a:two"));
        // A different connection's decisions are untouched.
        Assert.NotNull(cache.TryGet("conn-2", "svc-a:one"));
    }

    [Fact]
    public async Task Hub_OnDisconnected_DropsCachedDecisions()
    {
        var cache = new ChannelAuthDecisionCache();
        cache.StoreAllow("conn-1", "svc-a:room", "id");

        var ownership = new StubOwnership();
        var presence = new InMemoryPresenceRegistry();
        var hub = new GatewayHub(
            new StubAuthorization(),
            ownership,
            new StubAuthClient(),
            cache,
            new HubChannelMembership(),
            new MessageRateLimiter(new RealtimeRateLimitOptions()),
            new StubMessageClient(),
            presence,
            new PresenceEventCoalescer(presence, ownership, new NoopPublisher()),
            NullLogger<GatewayHub>.Instance)
        {
            Context = new StubContext("conn-1"),
        };

        Assert.NotNull(cache.TryGet("conn-1", "svc-a:room"));
        await hub.OnDisconnectedAsync(null);
        Assert.Null(cache.TryGet("conn-1", "svc-a:room"));
    }

    [Fact]
    public void AuthAttemptRateFloor_CapsWithinWindow_ResetsAfter()
    {
        // Review finding: denies are keyed per-credential, so a varying-credential
        // brute-force loop always misses the deny cache — the attempt window is what
        // stops it reaching the owner's auth endpoint once per round-trip.
        var clock = new TestClock();
        var cache = new ChannelAuthDecisionCache(clock, attemptWindow: TimeSpan.FromSeconds(10));

        for (var i = 0; i < ChannelAuthDecisionCache.MaxAuthAttemptsPerWindow; i++)
        {
            Assert.True(cache.TryRecordAuthAttempt("conn-1", "svc-a:room"));
        }

        // Over budget inside the window: refused without any callback.
        Assert.False(cache.TryRecordAuthAttempt("conn-1", "svc-a:room"));

        // A different channel (and a different connection) each have their own budget.
        Assert.True(cache.TryRecordAuthAttempt("conn-1", "svc-a:other"));
        Assert.True(cache.TryRecordAuthAttempt("conn-2", "svc-a:room"));

        // The window lapses: attempts are allowed again.
        clock.Now += TimeSpan.FromSeconds(11);
        Assert.True(cache.TryRecordAuthAttempt("conn-1", "svc-a:room"));
    }

    // ---- minimal stubs (this project carries no Moq) ----

    private sealed class StubContext : HubCallerContext
    {
        public StubContext(string id) => ConnectionId = id;
        public override string ConnectionId { get; }
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    private sealed class StubAuthorization : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            throw new NotSupportedException();

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            throw new NotSupportedException();
    }

    private sealed class StubOwnership : IChannelOwnershipResolver
    {
        public Task<ChannelOwner?> ResolveAsync(string prefix, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubAuthClient : IChannelAuthClient
    {
        public Task<ChannelAuthDecision> AuthorizeAsync(
            ChannelOwner owner, string channel, string? credential, string connectionId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubMessageClient : IChannelMessageClient
    {
        public Task<bool> ForwardAsync(
            ChannelOwner owner, string channel, string @event, object? data,
            string connectionId, string? identity, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopPublisher : IChannelEventPublisher
    {
        public Task PublishAsync(string channel, string @event, object data, CancellationToken ct = default) =>
            Task.CompletedTask;

        public void TryPublish(string channel, string @event, object data)
        {
        }
    }
}
