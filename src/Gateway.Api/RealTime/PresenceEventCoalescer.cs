using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.RealTime;

/// <summary>
/// Coalesces per-channel membership changes into a single <c>presence</c> event (task #612).
/// A burst of joins/leaves on one channel within a short window (<see cref="_window"/>, ~1s)
/// is collapsed into one broadcast — envelope event <c>presence</c>, data
/// <c>{ channel, count, joined:[{connectionId, identity}], left:[connectionId] }</c> — on the
/// same channel it describes. The event fires <b>only</b> when the owning service opted in
/// (<see cref="ChannelOwner.PresenceEnabled"/>): a presence broadcast leaks connection ids
/// (and any owner-supplied identity) to every subscriber, so it is off unless the owner chose
/// it. The owner presence API is unaffected — it is a token-gated read, not a broadcast.
/// <para>
/// The hub calls <see cref="RecordJoin"/>/<see cref="RecordLeave"/> synchronously (they never
/// touch the network); <see cref="PresenceCoalescerService"/> drives <see cref="FlushDueAsync"/>
/// on a short cadence. The opt-in check and the count read happen at flush, so the hot path
/// stays allocation-cheap and a channel that is not opted in simply has its buffered deltas
/// dropped. Best-effort throughout: the broadcast rides <see cref="IChannelEventPublisher.TryPublish"/>,
/// which swallows backplane failures.
/// </para>
/// </summary>
public sealed class PresenceEventCoalescer
{
    /// <summary>Default coalescing window — a burst within this collapses to one event.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(1);

    private readonly IPresenceRegistry _registry;
    private readonly IChannelOwnershipResolver _ownership;
    private readonly IChannelEventPublisher _publisher;
    private readonly TimeProvider _clock;
    private readonly ILogger<PresenceEventCoalescer>? _logger;
    private readonly TimeSpan _window;

    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    public PresenceEventCoalescer(
        IPresenceRegistry registry,
        IChannelOwnershipResolver ownership,
        IChannelEventPublisher publisher,
        TimeProvider? clock = null,
        ILogger<PresenceEventCoalescer>? logger = null,
        TimeSpan? window = null)
    {
        _registry = registry;
        _ownership = ownership;
        _publisher = publisher;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
        _window = window ?? DefaultWindow;
    }

    /// <summary>Buffer a join delta; the flush ~1s later emits one coalesced event.</summary>
    public void RecordJoin(string channel, string connectionId, string? identity) =>
        Record(channel, p =>
        {
            // A join cancels a pending leave for the same connection (net delta), and
            // overrides any earlier join identity with the latest.
            p.Left.Remove(connectionId);
            p.Joined[connectionId] = identity;
        });

    /// <summary>Buffer a leave delta; the flush ~1s later emits one coalesced event.</summary>
    public void RecordLeave(string channel, string connectionId) =>
        Record(channel, p =>
        {
            // A join then leave within the same window nets to nothing — the connection
            // was never announced, so it is neither in joined nor left.
            if (p.Joined.Remove(connectionId))
            {
                return;
            }

            p.Left.Add(connectionId);
        });

    private void Record(string channel, Action<Pending> mutate)
    {
        while (true)
        {
            var pending = _pending.GetOrAdd(channel, _ => new Pending());
            lock (pending.Gate)
            {
                // A concurrent flush may have drained-and-removed this instance while we
                // waited on its gate; retry so GetOrAdd hands back the fresh buffer.
                if (pending.Drained)
                {
                    continue;
                }

                mutate(pending);
                // Coalesce a burst: the window starts at the FIRST delta and does not slide,
                // so a run of joins flushes once ~1s after the first, not once per join.
                if (!pending.DueAt.HasValue)
                {
                    pending.DueAt = _clock.GetUtcNow() + _window;
                }

                return;
            }
        }
    }

    /// <summary>
    /// Flush every channel whose window has elapsed at <paramref name="now"/>. For each,
    /// drain its buffered deltas, and — only when the owning service opted in — broadcast a
    /// single coalesced <c>presence</c> event with the current member count. Never throws.
    /// </summary>
    public async Task FlushDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        foreach (var (channel, pending) in _pending)
        {
            List<KeyValuePair<string, string?>> joined;
            List<string> left;
            lock (pending.Gate)
            {
                if (!pending.DueAt.HasValue || pending.DueAt.Value > now)
                {
                    continue;
                }

                // Mark drained and remove THIS instance under the gate so a concurrent Record
                // (blocked on this gate) retries onto a fresh buffer rather than mutating a
                // buffer we are about to discard. TryRemove by pair removes only our instance.
                pending.Drained = true;
                _pending.TryRemove(new KeyValuePair<string, Pending>(channel, pending));
                joined = pending.Joined.ToList();
                left = pending.Left.ToList();
            }

            if (joined.Count == 0 && left.Count == 0)
            {
                continue;
            }

            await EmitAsync(channel, joined, left, ct).ConfigureAwait(false);
        }
    }

    private async Task EmitAsync(
        string channel, List<KeyValuePair<string, string?>> joined, List<string> left, CancellationToken ct)
    {
        try
        {
            // Opt-in gate at flush time: only the owning service that chose presence gets a
            // broadcast. ops:* and unowned prefixes resolve to no owner → no event.
            var prefix = GatewayHub.PrefixOf(channel);
            var owner = await _ownership.ResolveAsync(prefix, ct).ConfigureAwait(false);
            if (owner is null || !owner.PresenceEnabled)
            {
                return;
            }

            var count = await _registry.CountAsync(channel, ct).ConfigureAwait(false);
            var data = new
            {
                channel,
                count,
                joined = joined.Select(j => new { connectionId = j.Key, identity = j.Value }).ToArray(),
                left = left.ToArray(),
            };

            _publisher.TryPublish(channel, "presence", data);
        }
        catch (Exception ex)
        {
            // Presence is best-effort: a resolve/count failure must never surface.
            _logger?.LogWarning(
                ex, "Failed to emit coalesced presence event for channel '{Channel}'.", channel);
        }
    }

    private sealed class Pending
    {
        public readonly object Gate = new();
        public readonly Dictionary<string, string?> Joined = new(StringComparer.Ordinal);
        public readonly HashSet<string> Left = new(StringComparer.Ordinal);
        public DateTimeOffset? DueAt;

        /// <summary>Set once a flush has taken this buffer, so a racing Record retries.</summary>
        public bool Drained;
    }
}
