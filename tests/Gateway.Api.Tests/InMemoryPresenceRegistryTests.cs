using Gateway.Api.RealTime;

namespace Gateway.Api.Tests;

/// <summary>
/// Unit coverage for the default <see cref="InMemoryPresenceRegistry"/> (task #612): the
/// single-instance "who is in this channel" store. Join/leave/disconnect mutations, the
/// identity carried from the auth decision, the stamped join time, and the disconnect sweep
/// are all exercised against a fake clock — no Redis, no server.
/// </summary>
public class InMemoryPresenceRegistryTests
{
    [Fact]
    public async Task Add_ThenList_ReturnsEntry_WithIdentityAndJoinedAt()
    {
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UnixEpoch.AddMinutes(5) };
        var registry = new InMemoryPresenceRegistry(clock);

        await registry.AddAsync("svc-a:room", "conn-1", identity: "user-42");

        var members = await registry.ListAsync("svc-a:room");
        var entry = Assert.Single(members);
        Assert.Equal("conn-1", entry.ConnectionId);
        Assert.Equal("user-42", entry.Identity);
        Assert.Equal(clock.Now, entry.JoinedAt);
        Assert.Equal(1, await registry.CountAsync("svc-a:room"));
    }

    [Fact]
    public async Task Add_NullIdentity_IsPresent_WithNullIdentity()
    {
        var registry = new InMemoryPresenceRegistry();
        await registry.AddAsync("svc-a:public", "conn-1", identity: null);

        var entry = Assert.Single(await registry.ListAsync("svc-a:public"));
        Assert.Null(entry.Identity);
    }

    [Fact]
    public async Task Remove_DropsSingleMember_OthersRemain()
    {
        var registry = new InMemoryPresenceRegistry();
        await registry.AddAsync("svc-a:room", "conn-1", "a");
        await registry.AddAsync("svc-a:room", "conn-2", "b");

        await registry.RemoveAsync("svc-a:room", "conn-1");

        var members = await registry.ListAsync("svc-a:room");
        var entry = Assert.Single(members);
        Assert.Equal("conn-2", entry.ConnectionId);
        Assert.Equal(1, await registry.CountAsync("svc-a:room"));
    }

    [Fact]
    public async Task List_UnknownChannel_IsEmpty()
    {
        var registry = new InMemoryPresenceRegistry();
        Assert.Empty(await registry.ListAsync("svc-a:ghost"));
        Assert.Equal(0, await registry.CountAsync("svc-a:ghost"));
    }

    [Fact]
    public async Task RemoveConnection_SweepsEveryChannel_AndReturnsThem()
    {
        var registry = new InMemoryPresenceRegistry();
        await registry.AddAsync("svc-a:one", "conn-1", "id");
        await registry.AddAsync("svc-a:two", "conn-1", "id");
        await registry.AddAsync("svc-a:one", "conn-2", "id");

        var removed = await registry.RemoveConnectionAsync("conn-1");

        Assert.Equal(new[] { "svc-a:one", "svc-a:two" }, removed.OrderBy(c => c).ToArray());
        // conn-1 gone everywhere; conn-2 untouched.
        Assert.Equal(0, await registry.CountAsync("svc-a:two"));
        var remaining = Assert.Single(await registry.ListAsync("svc-a:one"));
        Assert.Equal("conn-2", remaining.ConnectionId);
    }

    [Fact]
    public async Task RemoveConnection_Unknown_ReturnsEmpty()
    {
        var registry = new InMemoryPresenceRegistry();
        Assert.Empty(await registry.RemoveConnectionAsync("conn-nobody"));
    }

    [Fact]
    public async Task ReAdd_KeepsFirstJoinedAt_RefreshesIdentity()
    {
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UnixEpoch };
        var registry = new InMemoryPresenceRegistry(clock);

        await registry.AddAsync("svc-a:room", "conn-1", "old");
        var first = (await registry.ListAsync("svc-a:room")).Single().JoinedAt;

        // A re-join later (e.g. a cached-allow re-JoinChannel) refreshes identity but not the
        // original join time — presence "joinedAt" is when the connection first arrived.
        clock.Now = clock.Now.AddMinutes(1);
        await registry.AddAsync("svc-a:room", "conn-1", "new");

        var entry = Assert.Single(await registry.ListAsync("svc-a:room"));
        Assert.Equal("new", entry.Identity);
        Assert.Equal(first, entry.JoinedAt);
    }
}
