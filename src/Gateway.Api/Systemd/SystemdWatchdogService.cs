using System.Net.Sockets;
using System.Text;
using Gateway.Api.Health;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Systemd;

/// <summary>
/// Wires the systemd unit's <c>WatchdogSec</c> to an application-level self-check
/// (tech-spec §6). When run under systemd with the watchdog enabled, systemd sets
/// <c>NOTIFY_SOCKET</c> and <c>WATCHDOG_USEC</c>; this service notifies readiness
/// (<c>READY=1</c>) and then, at half the watchdog interval, performs a self-check
/// (building the aggregated health report) and pings <c>WATCHDOG=1</c> only when the
/// check succeeds. If the process wedges, the ping stops and systemd restarts it
/// (the unit's <c>Restart=always</c>).
/// <para>
/// Outside systemd (no <c>NOTIFY_SOCKET</c> / <c>WATCHDOG_USEC</c>) it does nothing,
/// so tests and local <c>dotnet run</c> are unaffected.
/// </para>
/// </summary>
public sealed class SystemdWatchdogService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SystemdWatchdogService> _logger;

    public SystemdWatchdogService(IServiceScopeFactory scopeFactory, ILogger<SystemdWatchdogService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var socketPath = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
        var watchdogUsec = Environment.GetEnvironmentVariable("WATCHDOG_USEC");

        if (string.IsNullOrEmpty(socketPath)
            || !long.TryParse(watchdogUsec, out var usec)
            || usec <= 0)
        {
            _logger.LogDebug("systemd watchdog not configured; self-check disabled.");
            return;
        }

        // Ping at half the watchdog window so a single missed check does not trip it.
        var interval = TimeSpan.FromMicroseconds(usec / 2.0);

        Notify(socketPath, "READY=1");
        _logger.LogInformation("systemd watchdog self-check active; pinging every {Interval}.", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                // Self-check: the report must build within one watchdog window.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(interval);

                using var scope = _scopeFactory.CreateScope();
                var health = scope.ServiceProvider.GetRequiredService<HealthAggregator>();
                _ = await health.BuildAsync(timeout.Token);

                Notify(socketPath, "WATCHDOG=1");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Withhold the keep-alive ping; systemd will restart us if this persists.
                _logger.LogError(ex, "Watchdog self-check failed; withholding keep-alive ping.");
            }
        }
    }

    /// <summary>
    /// Send a datagram to systemd's notify socket. Supports both filesystem sockets
    /// and the abstract namespace (systemd encodes the latter with a leading <c>@</c>,
    /// which maps to a leading NUL). Best-effort: a send failure is logged, not thrown.
    /// </summary>
    private void Notify(string socketPath, string message)
    {
        try
        {
            var path = socketPath[0] == '@' ? '\0' + socketPath[1..] : socketPath;

            using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(path));
            socket.Send(Encoding.UTF8.GetBytes(message));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send systemd notification '{Message}'.", message);
        }
    }
}
