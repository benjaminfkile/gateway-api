using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.RealTime;

/// <summary>
/// Drives <see cref="PresenceEventCoalescer.FlushDueAsync"/> on a short cadence (task #612).
/// The coalescer buffers per-channel membership deltas behind a ~1s window; this loop is what
/// actually emits the collapsed <c>presence</c> event once a channel's window elapses. The
/// tick is well under the window so an event fires within roughly one window of the last
/// change. Cheap and always registered: when no channel opted in, every flush is a no-op.
/// </summary>
public sealed class PresenceCoalescerService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    private readonly PresenceEventCoalescer _coalescer;
    private readonly TimeProvider _clock;
    private readonly ILogger<PresenceCoalescerService> _logger;

    public PresenceCoalescerService(
        PresenceEventCoalescer coalescer,
        ILogger<PresenceCoalescerService> logger,
        TimeProvider? clock = null)
    {
        _coalescer = coalescer;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval, _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _coalescer.FlushDueAsync(_clock.GetUtcNow(), stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // FlushDueAsync already swallows per-channel failures; this is a last
                    // backstop so the loop never dies on an unexpected throw.
                    _logger.LogWarning(ex, "Presence coalescer flush tick failed; continuing.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
