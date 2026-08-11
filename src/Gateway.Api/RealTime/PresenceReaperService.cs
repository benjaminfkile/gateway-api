using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.RealTime;

/// <summary>
/// Periodically heartbeats this instance's presence rows and reaps stale ones from Redis
/// (task #612). Registered <b>only</b> when a Redis endpoint is configured — the in-memory
/// registry drops rows directly on disconnect and needs no reaper. Because a connection is
/// pinned to one instance and Redis (≤ 2.7) has no per-hash-field TTL, a crashed instance's
/// rows can only be removed by a survivor noticing the heartbeat went stale; this loop is
/// that survivor. Best-effort: <see cref="RedisPresenceRegistry.RefreshAndReapAsync"/> logs
/// and swallows transient Redis errors, and this loop never dies on a tick failure.
/// </summary>
public sealed class PresenceReaperService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly RedisPresenceRegistry _registry;
    private readonly ILogger<PresenceReaperService> _logger;

    public PresenceReaperService(RedisPresenceRegistry registry, ILogger<PresenceReaperService> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _registry.RefreshAndReapAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Presence reap tick failed; continuing.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
