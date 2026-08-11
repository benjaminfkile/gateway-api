using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Gateway.Api.RealTime;

/// <summary>
/// The fleet-aware <see cref="IPresenceRegistry"/> (task #612), selected exactly like the
/// SignalR Redis backplane — whenever <c>GATEWAY_REDIS_ENDPOINT</c> is set. Presence for a
/// channel is a Redis <b>hash per channel key</b> (<c>gw:presence:{channel}</c>), field =
/// connectionId, value = a small JSON row (identity, joinedAt, the owning instance, and a
/// heartbeat stamp). Every instance writes its own connections and unions the whole hash on
/// read, so "who is in this channel" spans the fleet, not just one box.
/// <para>
/// A connection is pinned to exactly one gateway instance, and Redis (≤ 2.7) has no
/// per-hash-field TTL — so a crashed instance's rows cannot expire on their own. Instead
/// each row carries a heartbeat stamp that its owning instance refreshes from
/// <see cref="RefreshAndReapAsync"/> (driven by <see cref="PresenceReaperService"/>): reads
/// hide rows whose heartbeat is older than <see cref="_staleAfter"/>, and the reaper deletes
/// them, so a crashed instance's connections age out within one stale window. This registry
/// is exercised in production, not in the offline test container; the in-memory
/// implementation is the one unit tests target (the selection gate is what tests assert).
/// </para>
/// </summary>
public sealed class RedisPresenceRegistry : IPresenceRegistry
{
    private const string KeyPrefix = "gw:presence:";
    private const string ChannelsSetKey = "gw:presence:channels";

    /// <summary>How stale a heartbeat may be before a row is hidden and reaped.</summary>
    private static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromSeconds(90);

    private readonly IConnectionMultiplexer _redis;
    private readonly TimeProvider _clock;
    private readonly ILogger<RedisPresenceRegistry>? _logger;
    private readonly string _instanceId;
    private readonly TimeSpan _staleAfter;

    // This instance's own (channel -> connectionId -> identity), so the reaper can
    // re-stamp exactly the rows it owns without re-reading them, and a disconnect sweep
    // is O(the connection's channels) rather than a scan of every channel key.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string?>> _localByChannel =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _localByConnection =
        new(StringComparer.Ordinal);

    public RedisPresenceRegistry(
        IConnectionMultiplexer redis,
        string instanceId,
        TimeProvider? clock = null,
        ILogger<RedisPresenceRegistry>? logger = null,
        TimeSpan? staleAfter = null)
    {
        _redis = redis;
        _instanceId = instanceId;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
        _staleAfter = staleAfter ?? DefaultStaleAfter;
    }

    private static string KeyFor(string channel) => KeyPrefix + channel;

    private IDatabase Db => _redis.GetDatabase();

    public async Task AddAsync(string channel, string connectionId, string? identity, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var db = Db;
        var key = KeyFor(channel);

        // Preserve the original joinedAt on a re-add: only overwrite it when the field is new.
        var existing = await db.HashGetAsync(key, connectionId).ConfigureAwait(false);
        var joinedAt = now;
        if (!existing.IsNullOrEmpty && TryParse(existing.ToString(), out var prior))
        {
            joinedAt = prior.JoinedAt;
        }

        var row = new PresenceRow(identity, joinedAt, _instanceId, now);
        await db.HashSetAsync(key, connectionId, Serialize(row)).ConfigureAwait(false);
        await db.SetAddAsync(ChannelsSetKey, channel).ConfigureAwait(false);
        // Extend the whole-key TTL so a channel whose every instance died is eventually
        // GC'd by Redis even if no surviving instance ever reaps it.
        await db.KeyExpireAsync(key, _staleAfter + _staleAfter).ConfigureAwait(false);

        TrackLocal(channel, connectionId, identity);
    }

    public async Task RemoveAsync(string channel, string connectionId, CancellationToken ct = default)
    {
        await Db.HashDeleteAsync(KeyFor(channel), connectionId).ConfigureAwait(false);
        UntrackLocal(channel, connectionId);
    }

    public async Task<IReadOnlyCollection<string>> RemoveConnectionAsync(string connectionId, CancellationToken ct = default)
    {
        if (!_localByConnection.TryRemove(connectionId, out var channels))
        {
            return Array.Empty<string>();
        }

        var db = Db;
        var removed = new List<string>(channels.Count);
        foreach (var channel in channels.Keys)
        {
            await db.HashDeleteAsync(KeyFor(channel), connectionId).ConfigureAwait(false);
            if (_localByChannel.TryGetValue(channel, out var conns))
            {
                conns.TryRemove(connectionId, out _);
                if (conns.IsEmpty)
                {
                    _localByChannel.TryRemove(channel, out _);
                }
            }

            removed.Add(channel);
        }

        return removed;
    }

    public async Task<IReadOnlyList<PresenceEntry>> ListAsync(string channel, CancellationToken ct = default)
    {
        var entries = await Db.HashGetAllAsync(KeyFor(channel)).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        var list = new List<PresenceEntry>(entries.Length);
        foreach (var entry in entries)
        {
            if (!TryParse(entry.Value.ToString(), out var row))
            {
                continue;
            }

            // Hide rows whose owning instance stopped heartbeating (crashed instance): they
            // are logically gone and the reaper will delete them.
            if (now - row.RefreshedAt > _staleAfter)
            {
                continue;
            }

            list.Add(new PresenceEntry(entry.Name.ToString(), row.Identity, row.JoinedAt));
        }

        return list;
    }

    public async Task<int> CountAsync(string channel, CancellationToken ct = default) =>
        (await ListAsync(channel, ct).ConfigureAwait(false)).Count;

    public Task<IReadOnlyList<ChannelMembership>> LocalMembershipsAsync(CancellationToken ct = default)
    {
        // Only THIS instance's own rows: eviction removes group membership and reads the
        // instance-local auth-decision cache, so each instance evicts only its own
        // connections (the fleet union in ListAsync would wrongly pull in peers' rows).
        var memberships = new List<ChannelMembership>();
        foreach (var (channel, conns) in _localByChannel)
        {
            foreach (var connectionId in conns.Keys)
            {
                memberships.Add(new ChannelMembership(channel, connectionId));
            }
        }

        return Task.FromResult<IReadOnlyList<ChannelMembership>>(memberships);
    }

    /// <summary>
    /// Re-stamp every row this instance owns (its heartbeat) and delete rows whose heartbeat
    /// has gone stale — a crashed instance's connections. Driven periodically by
    /// <see cref="PresenceReaperService"/>. Best-effort: a transient Redis error is logged,
    /// not thrown, so the reaper loop survives a blip.
    /// </summary>
    public async Task RefreshAndReapAsync(CancellationToken ct = default)
    {
        var db = Db;
        var now = _clock.GetUtcNow();

        // 1. Heartbeat this instance's own rows so surviving peers keep seeing them.
        foreach (var (channel, conns) in _localByChannel)
        {
            var key = KeyFor(channel);
            foreach (var (connectionId, identity) in conns)
            {
                var row = new PresenceRow(identity, now, _instanceId, now);
                await db.HashSetAsync(key, connectionId, Serialize(row)).ConfigureAwait(false);
            }

            await db.KeyExpireAsync(key, _staleAfter + _staleAfter).ConfigureAwait(false);
        }

        // 2. Sweep every known channel and delete rows whose heartbeat is stale.
        var channels = await db.SetMembersAsync(ChannelsSetKey).ConfigureAwait(false);
        foreach (var channelValue in channels)
        {
            var channel = channelValue.ToString();
            var key = KeyFor(channel);
            var entries = await db.HashGetAllAsync(key).ConfigureAwait(false);
            if (entries.Length == 0)
            {
                await db.SetRemoveAsync(ChannelsSetKey, channelValue).ConfigureAwait(false);
                continue;
            }

            var stale = new List<RedisValue>();
            foreach (var entry in entries)
            {
                if (!TryParse(entry.Value.ToString(), out var row) || now - row.RefreshedAt > _staleAfter)
                {
                    stale.Add(entry.Name);
                }
            }

            if (stale.Count > 0)
            {
                await db.HashDeleteAsync(key, stale.ToArray()).ConfigureAwait(false);
                _logger?.LogInformation(
                    "Reaped {Count} stale presence row(s) from channel '{Channel}'.", stale.Count, channel);
            }
        }
    }

    private void TrackLocal(string channel, string connectionId, string? identity)
    {
        var conns = _localByChannel.GetOrAdd(
            channel, _ => new ConcurrentDictionary<string, string?>(StringComparer.Ordinal));
        conns[connectionId] = identity;

        var chans = _localByConnection.GetOrAdd(
            connectionId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        chans[channel] = 0;
    }

    private void UntrackLocal(string channel, string connectionId)
    {
        if (_localByChannel.TryGetValue(channel, out var conns))
        {
            conns.TryRemove(connectionId, out _);
            if (conns.IsEmpty)
            {
                _localByChannel.TryRemove(channel, out _);
            }
        }

        if (_localByConnection.TryGetValue(connectionId, out var chans))
        {
            chans.TryRemove(channel, out _);
            if (chans.IsEmpty)
            {
                _localByConnection.TryRemove(connectionId, out _);
            }
        }
    }

    private static string Serialize(PresenceRow row) => JsonSerializer.Serialize(row);

    private static bool TryParse(string? value, out PresenceRow row)
    {
        if (string.IsNullOrEmpty(value))
        {
            row = default!;
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<PresenceRow>(value);
            if (parsed is not null)
            {
                row = parsed;
                return true;
            }
        }
        catch (JsonException)
        {
            // A row written by an incompatible version — treat as absent.
        }

        row = default!;
        return false;
    }

    /// <summary>The JSON stored per connection in a channel's presence hash.</summary>
    private sealed record PresenceRow(
        string? Identity, DateTimeOffset JoinedAt, string InstanceId, DateTimeOffset RefreshedAt);
}
