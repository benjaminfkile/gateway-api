using Gateway.Api.RealTime;

namespace Gateway.Api.Tests;

/// <summary>
/// Unit coverage for the channel-membership side-registry (task #611): SignalR exposes no
/// group-membership query, so the hub tracks which channels each connection joined (and the
/// auth-callback identity for each) here. <c>SendToChannel</c> consults it to enforce
/// "must be a member" (rule a) and to stamp the forward with who sent it.
/// </summary>
public class HubChannelMembershipTests
{
    [Fact]
    public void Join_ThenTryGet_ReturnsIdentity()
    {
        var m = new HubChannelMembership();
        m.Join("conn-1", "svc-a:room", identity: "user-42");

        Assert.True(m.TryGetIdentity("conn-1", "svc-a:room", out var identity));
        Assert.Equal("user-42", identity);
    }

    [Fact]
    public void Join_NullIdentity_IsMember_WithNullIdentity()
    {
        // Public / ops channels record a null identity but are still memberships.
        var m = new HubChannelMembership();
        m.Join("conn-1", "svc-a:public", identity: null);

        Assert.True(m.TryGetIdentity("conn-1", "svc-a:public", out var identity));
        Assert.Null(identity);
    }

    [Fact]
    public void TryGet_NotJoined_IsMiss()
    {
        var m = new HubChannelMembership();
        Assert.False(m.TryGetIdentity("conn-1", "svc-a:room", out var identity));
        Assert.Null(identity);
    }

    [Fact]
    public void Leave_RemovesSingleChannel_OthersRemain()
    {
        var m = new HubChannelMembership();
        m.Join("conn-1", "svc-a:one", "id");
        m.Join("conn-1", "svc-a:two", "id");

        m.Leave("conn-1", "svc-a:one");

        Assert.False(m.TryGetIdentity("conn-1", "svc-a:one", out _));
        Assert.True(m.TryGetIdentity("conn-1", "svc-a:two", out _));
    }

    [Fact]
    public void Drop_RemovesEveryMembership_ForConnection()
    {
        var m = new HubChannelMembership();
        m.Join("conn-1", "svc-a:one", "id");
        m.Join("conn-1", "svc-a:two", "id");
        m.Join("conn-2", "svc-a:one", "id");

        m.Drop("conn-1");

        Assert.False(m.TryGetIdentity("conn-1", "svc-a:one", out _));
        Assert.False(m.TryGetIdentity("conn-1", "svc-a:two", out _));
        Assert.True(m.TryGetIdentity("conn-2", "svc-a:one", out _));
    }

    [Fact]
    public void Join_Rejoin_OverwritesIdentity()
    {
        var m = new HubChannelMembership();
        m.Join("conn-1", "svc-a:room", "old");
        m.Join("conn-1", "svc-a:room", "new");

        Assert.True(m.TryGetIdentity("conn-1", "svc-a:room", out var identity));
        Assert.Equal("new", identity);
    }
}
