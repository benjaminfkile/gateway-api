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
/// </summary>
public sealed class InMemoryPresenceRegistry : IPresenceRegistry
{
    // channel -> (connectionId -> entry). The authoritative membership.
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
        var connections = _byChannel.GetOrAdd(
            channel, _ => new ConcurrentDictionary<string, PresenceEntry>(StringComparer.Ordinal));

        // Idempotent: a re-add (e.g. a re-join after a cached auth allow) keeps the first
        // JoinedAt and only refreshes the identity to the latest decision.
        connections.AddOrUpdate(
            connectionId,
            _ => new PresenceEntry(connectionId, identity, now),
            (_, existing) => existing with { Identity = identity });

        var channels = _byConnection.GetOrAdd(
            connectionId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        channels[channel] = 0;

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string channel, string connectionId, CancellationToken ct = default)
    {
        if (_byChannel.TryGetValue(channel, out var connections))
        {
            connections.TryRemove(connectionId, out _);
            // Drop the now-empty channel bucket so a churned channel never pins memory.
            if (connections.IsEmpty)
            {
                _byChannel.TryRemove(channel, out _);
            }
        }

        if (_byConnection.TryGetValue(connectionId, out var channels))
        {
            channels.TryRemove(channel, out _);
            if (channels.IsEmpty)
            {
                _byConnection.TryRemove(connectionId, out _);
            }
        }

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
            if (_byChannel.TryGetValue(channel, out var connections) && connections.TryRemove(connectionId, out _))
            {
                removed.Add(channel);
                if (connections.IsEmpty)
                {
                    _byChannel.TryRemove(channel, out _);
                }
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
        // both maps are moving views — so the eviction sweep iterates a stable list.
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
