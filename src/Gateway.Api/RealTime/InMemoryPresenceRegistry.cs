using System.Collections.Concurrent;

namespace Gateway.Api.RealTime;

/// <summary>
/// The default <see cref="IPresenceRegistry"/> (task #612): a thread-safe in-process map,
/// selected whenever <c>GATEWAY_REDIS_ENDPOINT</c> is unset — single-instance mode, where
/// one instance sees every connection so its local view is the whole truth. No Redis, no
/// backplane, no reaper: a disconnect (or the process ending) removes the rows directly.
/// <para>
/// Kept a singleton (the hub is per-invocation). A per-channel connection map answers
/// list/count; a reverse per-connection index makes the disconnect sweep O(channels the
/// connection was in) instead of a scan of every channel. <see cref="PresenceEntry.JoinedAt"/>
/// is stamped from an injectable <see cref="TimeProvider"/> so tests are deterministic.
/// </para>
/// <para>
/// Empty buckets are pruned so churned channel names never pin memory, but pruning and
/// insertion coordinate through a per-bucket lock with a re-check against the outer map
/// (review finding): a bare "IsEmpty → TryRemove(key)" raced a concurrent add that had
/// just repopulated the same bucket instance, unlinking a live member — invisible to the
/// owner presence API and events for the connection's lifetime.
/// </para>
/// </summary>
public sealed class InMemoryPresenceRegistry : IPresenceRegistry
{
    // channel -> (connectionId -> entry).
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PresenceEntry>> _byChannel =
        new(StringComparer.Ordinal);

    // connectionId -> set of channels, so RemoveConnectionAsync never scans _byChannel.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _byConnection =
        new(StringComparer.Ordinal);

    private readonly TimeProvider _clock;

    public InMemoryPresenceRegistry(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public Task AddAsync(string channel, string connectionId, string? identity, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        // Idempotent: a re-add (e.g. a re-join after a cached auth allow) keeps the first
        // JoinedAt and only refreshes the identity to the latest decision.
        PrunableBuckets.Insert(_byChannel, channel, bucket => bucket.AddOrUpdate(
            connectionId,
            _ => new PresenceEntry(connectionId, identity, now),
            (_, existing) => existing with { Identity = identity }));

        PrunableBuckets.Insert(_byConnection, connectionId, bucket => bucket[channel] = 0);

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string channel, string connectionId, CancellationToken ct = default)
    {
        PrunableBuckets.Remove(_byChannel, channel, connectionId);
        PrunableBuckets.Remove(_byConnection, connectionId, channel);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<string>> RemoveConnectionAsync(string connectionId, CancellationToken ct = default)
    {
        if (!_byConnection.TryRemove(connectionId, out var channels))
        {
            return Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());
        }

        var removed = new List<string>(channels.Count);
        foreach (var channel in channels.Keys)
        {
            if (PrunableBuckets.Remove(_byChannel, channel, connectionId))
            {
                removed.Add(channel);
            }
        }

        return Task.FromResult<IReadOnlyCollection<string>>(removed);
    }

    public Task<IReadOnlyList<PresenceEntry>> ListAsync(string channel, CancellationToken ct = default)
    {
        if (!_byChannel.TryGetValue(channel, out var connections))
        {
            return Task.FromResult<IReadOnlyList<PresenceEntry>>(Array.Empty<PresenceEntry>());
        }

        // Snapshot under enumeration; ConcurrentDictionary's enumerator is a moving view.
        IReadOnlyList<PresenceEntry> list = connections.Values.ToList();
        return Task.FromResult(list);
    }

    public Task<int> CountAsync(string channel, CancellationToken ct = default) =>
        Task.FromResult(_byChannel.TryGetValue(channel, out var connections) ? connections.Count : 0);

    public Task<IReadOnlyList<ChannelMembership>> LocalMembershipsAsync(CancellationToken ct = default)
    {
        // Single instance: every membership it holds is local. Snapshot under enumeration —
        // both maps are moving views — so callers iterate a stable list.
        var memberships = new List<ChannelMembership>();
        foreach (var (channel, connections) in _byChannel)
        {
            foreach (var connectionId in connections.Keys)
            {
                memberships.Add(new ChannelMembership(channel, connectionId));
            }
        }

        return Task.FromResult<IReadOnlyList<ChannelMembership>>(memberships);
    }
}
