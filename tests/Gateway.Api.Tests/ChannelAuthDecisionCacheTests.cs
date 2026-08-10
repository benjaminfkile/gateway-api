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
    public void StoreAllow_Cached_ForConnectionLifetime_WithIdentity()
    {
        var clock = new TestClock();
        var cache = new ChannelAuthDecisionCache(clock, TimeSpan.FromSeconds(10));

        cache.StoreAllow("conn-1", "svc-a:room", identity: "user-42");

        // Well past any denial TTL: an allow never expires while the connection lives.
        clock.Now += TimeSpan.FromHours(1);
        var decision = cache.TryGet("conn-1", "svc-a:room");
        Assert.NotNull(decision);
        Assert.True(decision!.Value.Allowed);
        Assert.Equal("user-42", decision.Value.Identity);
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

        var hub = new GatewayHub(
            new StubAuthorization(),
            new StubOwnership(),
            new StubAuthClient(),
            cache,
            NullLogger<GatewayHub>.Instance)
        {
            Context = new StubContext("conn-1"),
        };

        Assert.NotNull(cache.TryGet("conn-1", "svc-a:room"));
        await hub.OnDisconnectedAsync(null);
        Assert.Null(cache.TryGet("conn-1", "svc-a:room"));
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
}
