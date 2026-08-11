using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.RealTime;

/// <summary>
/// Drives <see cref="ChannelEvictionSweep.RunAsync"/> on a ~1 min cadence (task #613) — the
/// timer half of mid-connection channel eviction, split from the sweep logic exactly as
/// <see cref="PresenceCoalescerService"/> is split from <see cref="PresenceEventCoalescer"/>
/// so the decision logic is unit-testable offline against a fake clock. Always registered:
/// when nothing has expired and no service was removed, a tick is a cheap no-op (the sweep
/// short-circuits on an empty membership set). Best-effort — the sweep swallows per-step
/// failures and this loop never dies on an unexpected throw.
/// </summary>
public sealed class ChannelEvictionService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly ChannelEvictionSweep _sweep;
    private readonly TimeProvider _clock;
    private readonly ILogger<ChannelEvictionService> _logger;

    public ChannelEvictionService(
        ChannelEvictionSweep sweep,
        ILogger<ChannelEvictionService> logger,
        TimeProvider? clock = null)
    {
        _sweep = sweep;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _sweep.RunAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Channel eviction sweep tick failed; continuing.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
