using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Gateway.Api.RealTime;

/// <summary>
/// A delegated-auth decision for one <c>(connectionId, channel)</c> pair (task #594):
/// whether the join was admitted and, on an allow, the opaque identity string the
/// owning service returned (stored for phase 3 presence/messaging — nothing else
/// consumes it yet).
/// </summary>
public readonly record struct ChannelAuthDecision(bool Allowed, string? Identity)
{
    /// <summary>An admit decision carrying the service-supplied identity (may be null).</summary>
    public static ChannelAuthDecision Allow(string? identity) => new(true, identity);

    /// <summary>A reject decision. Carries no identity.</summary>
    public static readonly ChannelAuthDecision Deny = new(false, null);
}

/// <summary>
/// Per-connection cache of delegated channel-auth decisions (task #594, requirement 4).
/// An <b>allow</b> is cached for a finite window (~15 min) so a reconnect's group re-adds
/// or a client that repeatedly re-joins never re-hits the owning app, while still forcing
/// periodic re-authorization; a <b>deny</b> is cached only briefly (~10s) so a client
/// cannot be locked out for the connection's life yet a brute-force loop still can't hammer
/// the app every frame. A deny is keyed additionally on a hash of the presented credential
/// so a client that retries with a <i>different, now-valid</i> credential (the standard
/// auth-refresh flow) is not short-circuited by the stale deny — only a retry of the SAME
/// rejected credential is (task #608 finding 1). Every decision for a connection is dropped
/// when it disconnects (<see cref="Drop"/>, called from <c>GatewayHub.OnDisconnectedAsync</c>).
/// <para>
/// Singleton and thread-safe: the hub is instantiated per invocation, so the cache
/// that must outlive a single <c>JoinChannel</c> lives here, not on the hub. A dropped
/// connection id is tombstoned briefly so a late in-flight auth callback cannot resurrect
/// the map <see cref="Drop"/> already removed; per-connection entries are capped (oldest
/// evicted) and concurrent in-flight callbacks per connection are capped at one so a
/// join-loop cannot grow the cache unboundedly or hold multiple downstream slots
/// (task #608 finding 2).
/// </para>
/// </summary>
public sealed class ChannelAuthDecisionCache
{
    /// <summary>How long a deny is remembered before the next join re-hits the app.</summary>
    public static readonly TimeSpan DefaultDenialTtl = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long an allow is honoured before a RE-JOIN forces re-authorization. This bounds
    /// auth-callback load and makes voluntary re-joins re-check the credential — it does
    /// NOT revoke access mid-connection: SignalR group membership granted at join time
    /// keeps delivering until the socket closes (review finding; REALTIME.md documents
    /// this honestly). Mid-connection revocation needs membership tracking +
    /// RemoveFromGroupAsync, which arrives with the phase 3 presence registry.
    /// </summary>
    public static readonly TimeSpan DefaultAllowTtl = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long a dropped connection id is tombstoned so a late in-flight store — an auth
    /// callback that completed after the disconnect — is discarded rather than resurrecting
    /// the connection's map.
    /// </summary>
    public static readonly TimeSpan DefaultTombstoneTtl = TimeSpan.FromSeconds(30);

    /// <summary>Max distinct decisions cached per connection; the oldest is evicted past this.</summary>
    public const int MaxEntriesPerConnection = 64;

    /// <summary>
    /// Max delegated-auth callback attempts per <c>(connection, channel)</c> per
    /// <see cref="DefaultAttemptWindow"/>. Keying denies on the credential (task #608
    /// finding 1) means a varying-credential loop is always a deny-cache miss, so this
    /// rate floor — not the deny TTL — is what keeps a brute-force loop from reaching
    /// the owner's auth endpoint once per round-trip (review finding).
    /// </summary>
    public const int MaxAuthAttemptsPerWindow = 5;

    /// <summary>Window over which delegated-auth callback attempts are counted.</summary>
    public static readonly TimeSpan DefaultAttemptWindow = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _clock;
    private readonly TimeSpan _denialTtl;
    private readonly TimeSpan _allowTtl;
    private readonly TimeSpan _tombstoneTtl;
    private readonly TimeSpan _sweepInterval = TimeSpan.FromMinutes(1);

    // connectionId -> (key -> entry). Nested maps so a whole connection's decisions
    // drop in one operation on disconnect without scanning the entire cache. The inner
    // key namespaces allows (credential-independent) from denies (per-credential).
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Entry>> _byConnection =
        new(StringComparer.Ordinal);

    // Recently-dropped connection ids -> tombstone expiry. A store under a tombstoned id
    // is discarded (late in-flight callback after disconnect) so it cannot leak a map that
    // Drop already removed.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _tombstones = new(StringComparer.Ordinal);

    // Per-connection count of in-flight auth callbacks, capped at 1 so a join-loop on one
    // connection cannot hold multiple 2s downstream slots at once.
    private readonly ConcurrentDictionary<string, int> _inFlight = new(StringComparer.Ordinal);

    // (connectionId NUL channel) -> sliding attempt window for the callback rate floor.
    // Entries self-expire after the window and are also cleared by the sweep.
    private readonly ConcurrentDictionary<string, AttemptWindow> _attempts = new(StringComparer.Ordinal);

    // Next time an amortized sweep is due; claimed with CompareExchange so at most one
    // caller sweeps per interval (no dedicated timer — piggybacks on access).
    private long _nextSweepTicks;

    public ChannelAuthDecisionCache(
        TimeProvider? clock = null,
        TimeSpan? denialTtl = null,
        TimeSpan? allowTtl = null,
        TimeSpan? tombstoneTtl = null,
        TimeSpan? attemptWindow = null)
    {
        _clock = clock ?? TimeProvider.System;
        _denialTtl = denialTtl ?? DefaultDenialTtl;
        _allowTtl = allowTtl ?? DefaultAllowTtl;
        _tombstoneTtl = tombstoneTtl ?? DefaultTombstoneTtl;
        _attemptWindow = attemptWindow ?? DefaultAttemptWindow;
        _nextSweepTicks = (_clock.GetUtcNow() + _sweepInterval).UtcTicks;
    }

    private readonly TimeSpan _attemptWindow;

    /// <summary>
    /// Record one delegated-auth callback attempt for this pair and report whether it is
    /// within budget. Returns <c>false</c> once <see cref="MaxAuthAttemptsPerWindow"/>
    /// attempts have landed inside the current window — the caller must then deny WITHOUT
    /// consulting the owning service. This is the brute-force floor the per-credential
    /// deny key cannot provide (a varying credential always misses the deny cache).
    /// </summary>
    public bool TryRecordAuthAttempt(string connectionId, string channel)
    {
        var key = connectionId + "\0" + channel;
        var now = _clock.GetUtcNow();
        while (true)
        {
            if (!_attempts.TryGetValue(key, out var window))
            {
                if (_attempts.TryAdd(key, new AttemptWindow(now, 1)))
                {
                    return true;
                }

                continue;
            }

            if (now - window.StartedAt >= _attemptWindow)
            {
                // Window lapsed; start a fresh one.
                if (_attempts.TryUpdate(key, new AttemptWindow(now, 1), window))
                {
                    return true;
                }

                continue;
            }

            if (window.Count >= MaxAuthAttemptsPerWindow)
            {
                return false;
            }

            if (_attempts.TryUpdate(key, window with { Count = window.Count + 1 }, window))
            {
                return true;
            }
        }
    }

    /// <summary>
    /// A still-valid cached decision for this pair, or null on a miss. An allow is
    /// credential-independent (keyed per <c>(connection, channel)</c>) and honoured until
    /// its finite TTL; a deny is keyed additionally on the credential so it only
    /// short-circuits a retry of the SAME rejected credential, and only within its short
    /// TTL. An expired entry is treated as a miss and evicted.
    /// </summary>
    public ChannelAuthDecision? TryGet(string connectionId, string channel, string? credential = null)
    {
        MaybeSweep();

        if (!_byConnection.TryGetValue(connectionId, out var channels))
        {
            return null;
        }

        var now = _clock.GetUtcNow();

        // An allow admits any credential for this channel until it expires.
        var allowKey = AllowKey(channel);
        if (channels.TryGetValue(allowKey, out var allow))
        {
            if (now < allow.ExpiresAt)
            {
                return allow.Decision;
            }

            channels.TryRemove(allowKey, out _);
        }

        // A deny only short-circuits a retry of the same credential, within its TTL.
        var denyKey = DenyKey(channel, credential);
        if (channels.TryGetValue(denyKey, out var deny))
        {
            if (now < deny.ExpiresAt)
            {
                return deny.Decision;
            }

            channels.TryRemove(denyKey, out _);
        }

        return null;
    }

    /// <summary>Cache an admit for the finite allow TTL (credential-independent).</summary>
    public void StoreAllow(string connectionId, string channel, string? identity) =>
        Store(connectionId, AllowKey(channel), new Entry(
            ChannelAuthDecision.Allow(identity), _clock.GetUtcNow() + _allowTtl, _clock.GetUtcNow()));

    /// <summary>
    /// Cache a reject for the short denial TTL (brute-force blunt), keyed on a hash of the
    /// presented credential so a retry with a different, now-valid credential is not blocked.
    /// </summary>
    public void StoreDeny(string connectionId, string channel, string? credential = null) =>
        Store(connectionId, DenyKey(channel, credential), new Entry(
            ChannelAuthDecision.Deny, _clock.GetUtcNow() + _denialTtl, _clock.GetUtcNow()));

    /// <summary>Forget every decision for a connection (called on disconnect).</summary>
    public void Drop(string connectionId)
    {
        // Tombstone BEFORE removing the map so a store racing this disconnect (its
        // re-check below) sees the tombstone and does not leak a resurrected map.
        _tombstones[connectionId] = _clock.GetUtcNow() + _tombstoneTtl;
        _byConnection.TryRemove(connectionId, out _);
        _inFlight.TryRemove(connectionId, out _);
    }

    /// <summary>
    /// Try to claim the single in-flight auth-callback slot for a connection. Returns false
    /// when a callback is already running for this connection (cap = 1): the caller must not
    /// open a second downstream slot. Always pair a <c>true</c> with <see cref="EndAuthCallback"/>.
    /// </summary>
    public bool TryBeginAuthCallback(string connectionId)
    {
        var updated = _inFlight.AddOrUpdate(connectionId, 1, (_, current) => current + 1);
        if (updated > 1)
        {
            // Someone already holds the slot; roll back our increment and refuse.
            // (Same remove-at-zero rule as EndAuthCallback — never store a 0.)
            DecrementInFlight(connectionId);
            return false;
        }

        return true;
    }

    /// <summary>Release the in-flight auth-callback slot claimed by <see cref="TryBeginAuthCallback"/>.</summary>
    public void EndAuthCallback(string connectionId) => DecrementInFlight(connectionId);

    /// <summary>
    /// Decrement a connection's in-flight count, REMOVING the entry at zero instead of
    /// storing 0. AddOrUpdate here would re-insert an entry that <see cref="Drop"/> already
    /// removed (disconnect racing an in-flight callback) — connection ids are never reused,
    /// so that zero-valued entry would live for the process lifetime (review finding).
    /// </summary>
    private void DecrementInFlight(string connectionId)
    {
        while (_inFlight.TryGetValue(connectionId, out var current))
        {
            var next = current > 1 ? current - 1 : 0;
            if (next == 0)
            {
                if (_inFlight.TryRemove(new KeyValuePair<string, int>(connectionId, current)))
                {
                    return;
                }
            }
            else if (_inFlight.TryUpdate(connectionId, next, current))
            {
                return;
            }
        }
    }

    private void Store(string connectionId, string key, Entry entry)
    {
        // Discard a store under a dropped (tombstoned) connection id.
        if (IsTombstoned(connectionId))
        {
            return;
        }

        var channels = _byConnection.GetOrAdd(
            connectionId, _ => new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal));
        channels[key] = entry;

        // Drop may have raced between the tombstone check and GetOrAdd, re-creating the
        // map here; if the id is (now) tombstoned, undo so a late store cannot leak.
        if (IsTombstoned(connectionId))
        {
            _byConnection.TryRemove(connectionId, out _);
            return;
        }

        EnforceCap(channels);
        MaybeSweep();
    }

    /// <summary>Cap per-connection entries, evicting the oldest-created past the limit.</summary>
    private static void EnforceCap(ConcurrentDictionary<string, Entry> channels)
    {
        if (channels.Count <= MaxEntriesPerConnection)
        {
            return;
        }

        // Serialize the compound "find oldest + remove" so two stores cannot both under-evict.
        lock (channels)
        {
            while (channels.Count > MaxEntriesPerConnection)
            {
                string? oldestKey = null;
                var oldest = DateTimeOffset.MaxValue;
                foreach (var kv in channels)
                {
                    if (kv.Value.CreatedAt < oldest)
                    {
                        oldest = kv.Value.CreatedAt;
                        oldestKey = kv.Key;
                    }
                }

                if (oldestKey is null || !channels.TryRemove(oldestKey, out _))
                {
                    break;
                }
            }
        }
    }

    private bool IsTombstoned(string connectionId)
    {
        if (_tombstones.TryGetValue(connectionId, out var until))
        {
            if (_clock.GetUtcNow() < until)
            {
                return true;
            }

            _tombstones.TryRemove(connectionId, out _);
        }

        return false;
    }

    /// <summary>
    /// Amortized sweep: at most once per <see cref="_sweepInterval"/>, drop expired entries,
    /// empty connection maps, and expired tombstones so nothing lives for the process life.
    /// </summary>
    private void MaybeSweep()
    {
        var now = _clock.GetUtcNow();
        var next = Interlocked.Read(ref _nextSweepTicks);
        if (now.UtcTicks < next)
        {
            return;
        }

        // Claim this sweep window; a loser just skips (the winner sweeps for everyone).
        if (Interlocked.CompareExchange(ref _nextSweepTicks, (now + _sweepInterval).UtcTicks, next) != next)
        {
            return;
        }

        foreach (var (connId, channels) in _byConnection)
        {
            foreach (var (key, entry) in channels)
            {
                if (now >= entry.ExpiresAt)
                {
                    channels.TryRemove(key, out _);
                }
            }

            if (channels.IsEmpty && !IsTombstoned(connId))
            {
                _byConnection.TryRemove(connId, out _);
            }
        }

        foreach (var (connId, until) in _tombstones)
        {
            if (now >= until)
            {
                _tombstones.TryRemove(connId, out _);
            }
        }

        // Expired attempt windows (rate floor) and any zero-valued in-flight stragglers:
        // belt-and-braces so neither map can grow for the process lifetime.
        foreach (var (key, window) in _attempts)
        {
            if (now - window.StartedAt >= _attemptWindow)
            {
                _attempts.TryRemove(new KeyValuePair<string, AttemptWindow>(key, window));
            }
        }

        foreach (var (connId, count) in _inFlight)
        {
            if (count <= 0)
            {
                _inFlight.TryRemove(new KeyValuePair<string, int>(connId, count));
            }
        }
    }

    /// <summary>Inner key for a credential-independent allow.</summary>
    private static string AllowKey(string channel) => "a\0" + channel;

    /// <summary>Inner key for a deny, namespaced by channel and a hash of the credential.</summary>
    private static string DenyKey(string channel, string? credential) =>
        "d\0" + channel + "\0" + HashCredential(credential);

    /// <summary>
    /// A stable, non-reversible fingerprint of the credential for the deny key. The raw
    /// credential is never stored — only its SHA-256 hex — so the cache never holds a
    /// service's opaque secret (task #608 finding 1).
    /// </summary>
    private static string HashCredential(string? credential)
    {
        if (credential is null)
        {
            return "\0null";
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
    }

    private readonly record struct Entry(ChannelAuthDecision Decision, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt);

    private readonly record struct AttemptWindow(DateTimeOffset StartedAt, int Count);
}
