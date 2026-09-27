using System.Reflection;
using Gateway.Api.RealTime;
using StackExchange.Redis;

namespace Gateway.Api.Tests;

/// <summary>
/// Unit coverage for <see cref="RedisPresenceRegistry"/> against an in-process fake of the
/// Redis hash and set commands it issues, so no Redis server is needed. The fake records every
/// command name, which lets the count tests assert that <c>CountAsync</c> reads only the hash
/// length and never fetches the member rows.
/// </summary>
public class RedisPresenceRegistryTests
{
    [Fact]
    public async Task Count_MatchesList_AfterJoinsLeavesAndDisconnectSweep()
    {
        var redis = FakeRedis.Create(out var state);
        var registry = new RedisPresenceRegistry(redis, "instance-1");

        await registry.AddAsync("svc-a:room", "conn-1", "a");
        await registry.AddAsync("svc-a:room", "conn-2", "b");
        await registry.AddAsync("svc-a:other", "conn-1", "a");
        Assert.Equal(2, await registry.CountAsync("svc-a:room"));
        Assert.Equal((await registry.ListAsync("svc-a:room")).Count, await registry.CountAsync("svc-a:room"));

        await registry.RemoveAsync("svc-a:room", "conn-2");
        Assert.Equal(1, await registry.CountAsync("svc-a:room"));
        Assert.Equal(1, await registry.CountAsync("svc-a:other"));

        await registry.RemoveConnectionAsync("conn-1");
        Assert.Equal(0, await registry.CountAsync("svc-a:room"));
        Assert.Equal(0, await registry.CountAsync("svc-a:other"));
        Assert.Empty(await registry.ListAsync("svc-a:room"));
    }

    [Fact]
    public async Task Count_UnknownChannel_IsZero()
    {
        var registry = new RedisPresenceRegistry(FakeRedis.Create(out _), "instance-1");
        Assert.Equal(0, await registry.CountAsync("svc-a:empty"));
    }

    [Fact]
    public async Task Count_ReadsHashLength_WithoutFetchingMembers()
    {
        var redis = FakeRedis.Create(out var state);
        var registry = new RedisPresenceRegistry(redis, "instance-1");
        for (var i = 0; i < 50; i++)
        {
            await registry.AddAsync("svc-a:room", $"conn-{i}", identity: null);
        }

        state.Commands.Clear();
        Assert.Equal(50, await registry.CountAsync("svc-a:room"));

        var command = Assert.Single(state.Commands);
        Assert.Equal(nameof(IDatabaseAsync.HashLengthAsync), command);
    }

    [Fact]
    public async Task Count_IncludesUnreapedStaleRows_UntilTheReaperPrunesThem()
    {
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UnixEpoch };
        var staleAfter = TimeSpan.FromSeconds(90);
        var redis = FakeRedis.Create(out _);
        var crashed = new RedisPresenceRegistry(redis, "instance-crashed", clock, staleAfter: staleAfter);
        var survivor = new RedisPresenceRegistry(redis, "instance-survivor", clock, staleAfter: staleAfter);

        await crashed.AddAsync("svc-a:room", "conn-crashed", identity: null);
        await survivor.AddAsync("svc-a:room", "conn-live", identity: null);

        // The crashed instance stops heartbeating. The list hides its row at once; the count is
        // the raw hash length, so it still includes the row until the reaper deletes it.
        // The survivor heartbeats midway, so only the crashed row crosses the stale window.
        clock.Now += staleAfter / 2;
        await survivor.RefreshAndReapAsync();
        clock.Now += staleAfter / 2 + TimeSpan.FromSeconds(1);
        Assert.Single(await survivor.ListAsync("svc-a:room"));
        Assert.Equal(2, await survivor.CountAsync("svc-a:room"));

        await survivor.RefreshAndReapAsync();
        Assert.Equal(1, await survivor.CountAsync("svc-a:room"));
        Assert.Equal("conn-live", Assert.Single(await survivor.ListAsync("svc-a:room")).ConnectionId);
    }

    /// <summary>The fake's backing store plus the ordered log of database command names.</summary>
    internal sealed class FakeRedisState
    {
        public Dictionary<string, Dictionary<string, RedisValue>> Hashes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, HashSet<string>> Sets { get; } = new(StringComparer.Ordinal);
        public List<string> Commands { get; } = new();
    }

    /// <summary>
    /// A <see cref="DispatchProxy"/> standing in for both <see cref="IConnectionMultiplexer"/>
    /// (whose <c>GetDatabase</c> returns the fake database) and <see cref="IDatabase"/>
    /// (which implements the hash, set, and expiry commands the registry uses). Any other
    /// member throws, so an unexpected command fails the test loudly.
    /// </summary>
    public class FakeRedis : DispatchProxy
    {
        private FakeRedisState _state = null!;
        private IDatabase? _database;

        internal static IConnectionMultiplexer Create(out FakeRedisState state)
        {
            state = new FakeRedisState();
            var database = Create<IDatabase, FakeRedis>();
            ((FakeRedis)(object)database)._state = state;

            var multiplexer = Create<IConnectionMultiplexer, FakeRedis>();
            var proxy = (FakeRedis)(object)multiplexer;
            proxy._state = state;
            proxy._database = database;
            return multiplexer;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod!.Name;
            args ??= Array.Empty<object?>();

            if (name == nameof(IConnectionMultiplexer.GetDatabase) && _database is not null)
            {
                return _database;
            }

            _state.Commands.Add(name);
            switch (name)
            {
                case nameof(IDatabaseAsync.HashGetAsync) when args[1] is RedisValue field:
                    return Task.FromResult(
                        Hash((RedisKey)args[0]!, create: false)?.GetValueOrDefault(field.ToString()) ?? RedisValue.Null);

                case nameof(IDatabaseAsync.HashSetAsync) when args[1] is RedisValue field:
                {
                    var added = Hash((RedisKey)args[0]!, create: true)!.TryAdd(field.ToString(), (RedisValue)args[2]!);
                    if (!added)
                    {
                        Hash((RedisKey)args[0]!, create: true)![field.ToString()] = (RedisValue)args[2]!;
                    }

                    return Task.FromResult(added);
                }

                case nameof(IDatabaseAsync.HashDeleteAsync) when args[1] is RedisValue field:
                    return Task.FromResult(Hash((RedisKey)args[0]!, create: false)?.Remove(field.ToString()) ?? false);

                case nameof(IDatabaseAsync.HashDeleteAsync) when args[1] is RedisValue[] fields:
                {
                    var hash = Hash((RedisKey)args[0]!, create: false);
                    return Task.FromResult(hash is null ? 0L : fields.LongCount(f => hash.Remove(f.ToString())));
                }

                case nameof(IDatabaseAsync.HashGetAllAsync):
                {
                    var hash = Hash((RedisKey)args[0]!, create: false);
                    var entries = hash is null
                        ? Array.Empty<HashEntry>()
                        : hash.Select(kv => new HashEntry(kv.Key, kv.Value)).ToArray();
                    return Task.FromResult(entries);
                }

                case nameof(IDatabaseAsync.HashLengthAsync):
                    return Task.FromResult((long)(Hash((RedisKey)args[0]!, create: false)?.Count ?? 0));

                case nameof(IDatabaseAsync.SetAddAsync) when args[1] is RedisValue member:
                {
                    var key = ((RedisKey)args[0]!).ToString();
                    if (!_state.Sets.TryGetValue(key, out var set))
                    {
                        _state.Sets[key] = set = new HashSet<string>(StringComparer.Ordinal);
                    }

                    return Task.FromResult(set.Add(member.ToString()));
                }

                case nameof(IDatabaseAsync.SetMembersAsync):
                {
                    var key = ((RedisKey)args[0]!).ToString();
                    var members = _state.Sets.TryGetValue(key, out var set)
                        ? set.Select(m => (RedisValue)m).ToArray()
                        : Array.Empty<RedisValue>();
                    return Task.FromResult(members);
                }

                case nameof(IDatabaseAsync.SetRemoveAsync) when args[1] is RedisValue member:
                {
                    var key = ((RedisKey)args[0]!).ToString();
                    return Task.FromResult(_state.Sets.TryGetValue(key, out var set) && set.Remove(member.ToString()));
                }

                case nameof(IDatabaseAsync.KeyExpireAsync):
                    return Task.FromResult(true);

                default:
                    throw new NotSupportedException($"The fake Redis does not implement {name}.");
            }
        }

        private Dictionary<string, RedisValue>? Hash(RedisKey key, bool create)
        {
            var name = key.ToString();
            if (_state.Hashes.TryGetValue(name, out var hash))
            {
                return hash;
            }

            if (!create)
            {
                return null;
            }

            return _state.Hashes[name] = new Dictionary<string, RedisValue>(StringComparer.Ordinal);
        }
    }
}
