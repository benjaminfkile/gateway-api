using System.Collections.Concurrent;
using System.Text.Json;
using Gateway.Api.RealTime;

namespace Gateway.Api.Tests;

/// <summary>
/// Unit coverage for <see cref="PresenceEventCoalescer"/> (task #612): a burst of membership
/// changes on one channel collapses into a single <c>presence</c> event, but ONLY when the
/// owning service opted in. Driven by a fake clock and <see cref="FlushDueAsync"/> directly
/// (the background flush loop just calls that), so coalescing is deterministic offline.
/// </summary>
public class PresenceEventCoalescerTests
{
    private sealed class CapturingPublisher : IChannelEventPublisher
    {
        public readonly ConcurrentQueue<(string Channel, string Event, JsonElement Data)> Events = new();

        public Task PublishAsync(string channel, string @event, object data, CancellationToken ct = default)
        {
            Capture(channel, @event, data);
            return Task.CompletedTask;
        }

        public void TryPublish(string channel, string @event, object data) => Capture(channel, @event, data);

        private void Capture(string channel, string @event, object data)
        {
            // Serialize by RUNTIME type — the coalescer hands an anonymous object typed as
            // `object`, and the generic overload would serialize the empty `object` shape.
            var json = JsonSerializer.SerializeToElement(data, data.GetType());
            Events.Enqueue((channel, @event, json));
        }
    }

    private sealed class StubOwnership : IChannelOwnershipResolver
    {
        private readonly Dictionary<string, ChannelOwner> _owners = new(StringComparer.Ordinal);

        public StubOwnership Add(string prefix, bool presenceEnabled)
        {
            _owners[prefix] = new ChannelOwner(prefix, "tok", null, null, presenceEnabled, 8080);
            return this;
        }

        public Task<ChannelOwner?> ResolveAsync(string prefix, CancellationToken ct = default) =>
            Task.FromResult(_owners.TryGetValue(prefix, out var o) ? o : null);
    }

    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private static (PresenceEventCoalescer Coalescer, InMemoryPresenceRegistry Registry, CapturingPublisher Publisher, ManualTimeProvider Clock)
        Build(StubOwnership ownership)
    {
        var clock = new ManualTimeProvider { Now = T0 };
        var registry = new InMemoryPresenceRegistry(clock);
        var publisher = new CapturingPublisher();
        var coalescer = new PresenceEventCoalescer(registry, ownership, publisher, clock, logger: null, window: Window);
        return (coalescer, registry, publisher, clock);
    }

    [Fact]
    public async Task BurstOfJoins_OptedIn_CoalescesIntoOneEvent()
    {
        var ownership = new StubOwnership().Add("svc-a", presenceEnabled: true);
        var (coalescer, registry, publisher, clock) = Build(ownership);

        // Two joins land in the registry and buffer two deltas inside one window.
        await registry.AddAsync("svc-a:room", "conn-1", "user-1");
        await registry.AddAsync("svc-a:room", "conn-2", "user-2");
        coalescer.RecordJoin("svc-a:room", "conn-1", "user-1");
        coalescer.RecordJoin("svc-a:room", "conn-2", "user-2");

        // Before the window elapses, nothing fires.
        await coalescer.FlushDueAsync(clock.Now);
        Assert.Empty(publisher.Events);

        // After the window, exactly ONE coalesced event carries both joins and the count.
        clock.Now = T0 + Window;
        await coalescer.FlushDueAsync(clock.Now);

        var evt = Assert.Single(publisher.Events);
        Assert.Equal("svc-a:room", evt.Channel);
        Assert.Equal("presence", evt.Event);
        Assert.Equal("svc-a:room", evt.Data.GetProperty("channel").GetString());
        Assert.Equal(2, evt.Data.GetProperty("count").GetInt32());

        var joined = evt.Data.GetProperty("joined").EnumerateArray().ToList();
        Assert.Equal(2, joined.Count);
        var ids = joined.Select(j => j.GetProperty("connectionId").GetString()).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "conn-1", "conn-2" }, ids);
        Assert.Contains(joined, j => j.GetProperty("identity").GetString() == "user-1");
        Assert.Empty(evt.Data.GetProperty("left").EnumerateArray());

        // The window is one-shot: a second flush with no new deltas emits nothing.
        clock.Now = T0 + Window + Window;
        await coalescer.FlushDueAsync(clock.Now);
        Assert.Single(publisher.Events);
    }

    [Fact]
    public async Task NotOptedIn_EmitsNothing()
    {
        var ownership = new StubOwnership().Add("svc-a", presenceEnabled: false);
        var (coalescer, _, publisher, clock) = Build(ownership);

        coalescer.RecordJoin("svc-a:room", "conn-1", null);
        clock.Now = T0 + Window;
        await coalescer.FlushDueAsync(clock.Now);

        Assert.Empty(publisher.Events);
    }

    [Fact]
    public async Task UnownedOrOpsChannel_EmitsNothing()
    {
        // No owner registered for "ops" or "ghost" → resolve returns null → no event.
        var ownership = new StubOwnership();
        var (coalescer, _, publisher, clock) = Build(ownership);

        coalescer.RecordJoin("ops:fleet", "conn-1", null);
        coalescer.RecordJoin("ghost:x", "conn-2", null);
        clock.Now = T0 + Window;
        await coalescer.FlushDueAsync(clock.Now);

        Assert.Empty(publisher.Events);
    }

    [Fact]
    public async Task Leave_EmitsLeftConnectionId()
    {
        var ownership = new StubOwnership().Add("svc-a", presenceEnabled: true);
        var (coalescer, _, publisher, clock) = Build(ownership);

        coalescer.RecordLeave("svc-a:room", "conn-9");
        clock.Now = T0 + Window;
        await coalescer.FlushDueAsync(clock.Now);

        var evt = Assert.Single(publisher.Events);
        var left = evt.Data.GetProperty("left").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "conn-9" }, left);
        Assert.Empty(evt.Data.GetProperty("joined").EnumerateArray());
        Assert.Equal(0, evt.Data.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task JoinThenLeave_SameConnection_WithinWindow_NetsToNothing()
    {
        var ownership = new StubOwnership().Add("svc-a", presenceEnabled: true);
        var (coalescer, _, publisher, clock) = Build(ownership);

        // A connection that joins and leaves inside one window was never announced, so the
        // coalesced delta is empty and no event fires.
        coalescer.RecordJoin("svc-a:room", "conn-1", "user-1");
        coalescer.RecordLeave("svc-a:room", "conn-1");
        clock.Now = T0 + Window;
        await coalescer.FlushDueAsync(clock.Now);

        Assert.Empty(publisher.Events);
    }

    [Fact]
    public async Task SecondWindow_AfterFlush_EmitsAgain()
    {
        var ownership = new StubOwnership().Add("svc-a", presenceEnabled: true);
        var (coalescer, registry, publisher, clock) = Build(ownership);

        await registry.AddAsync("svc-a:room", "conn-1", null);
        coalescer.RecordJoin("svc-a:room", "conn-1", null);
        clock.Now = T0 + Window;
        await coalescer.FlushDueAsync(clock.Now);
        Assert.Single(publisher.Events);

        // A later change starts a fresh window and flushes independently.
        coalescer.RecordLeave("svc-a:room", "conn-1");
        clock.Now = clock.Now + Window;
        await coalescer.FlushDueAsync(clock.Now);
        Assert.Equal(2, publisher.Events.Count);
    }
}
