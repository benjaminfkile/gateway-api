using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Gateway.Api.RealTime;

/// <summary>
/// Global SignalR hub filter applying a per-connection token bucket to EVERY client
/// hub-method invocation (review finding): rate limiting was per-method opt-in —
/// <c>SendToChannel</c> checked its bucket, while a <c>JoinChannel</c>/<c>LeaveChannel</c>
/// spam loop was unthrottled despite each iteration costing Redis round-trips, backplane
/// group churn, and coalescer buffering. A filter makes coverage the DEFAULT: any future
/// client-callable method ships limited unless deliberately exempted.
/// <para>
/// The budget (default 20/s, burst 40 — see <see cref="RealtimeRateLimitOptions"/>) sits
/// far above legitimate use and far above the tighter per-concern limits layered beneath
/// it (message bucket, delegated-auth attempt window), which stay in place: this filter
/// is the coarse outer wall, not a replacement. Buckets drop on disconnect so the map
/// never grows for the process lifetime.
/// </para>
/// </summary>
public sealed class HubInvocationRateLimitFilter : IHubFilter
{
    /// <summary>The generic error a throttled invocation surfaces to the client.</summary>
    public const string ThrottledMessage = "Rate limited: too many hub invocations; slow down and retry.";

    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new(StringComparer.Ordinal);
    private readonly double _rate;
    private readonly double _burst;
    private readonly TimeProvider _clock;

    public HubInvocationRateLimitFilter(RealtimeRateLimitOptions options, TimeProvider? clock = null)
    {
        _rate = options.InvocationRate;
        _burst = options.InvocationBurst;
        _clock = clock ?? TimeProvider.System;
    }

    public ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var connectionId = invocationContext.Context.ConnectionId;
        var bucket = _buckets.GetOrAdd(connectionId, _ => new TokenBucket(_rate, _burst, _clock));
        if (!bucket.TryTake(out _))
        {
            throw new HubException(ThrottledMessage);
        }

        return next(invocationContext);
    }

    public async Task OnDisconnectedAsync(
        HubLifetimeContext context,
        Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        _buckets.TryRemove(context.Context.ConnectionId, out _);
        await next(context, exception);
    }
}
