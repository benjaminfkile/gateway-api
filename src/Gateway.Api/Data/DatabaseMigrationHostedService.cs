using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Data;

/// <summary>
/// Applies EF Core migrations before anything reads the schema (tech-spec §6, and
/// this task's requirements 1–3). Registered <b>before</b> the reconciler so that,
/// with the host starting hosted services sequentially, its <see cref="StartAsync"/>
/// completes first; it also opens <see cref="MigrationReadinessGate"/> so the
/// reconciler/heartbeat loops explicitly wait for migration completion rather than
/// racing a not-yet-created schema.
/// <para>
/// Resilience (requirement 3): the box may boot before the database is reachable,
/// so each attempt is retried with exponential backoff for a bounded window
/// (<see cref="MigrationOptions.MaxWait"/>). If the window closes while still
/// failing, it logs clearly and throws — aborting host startup so the process
/// exits non-zero and systemd restarts it, rather than serving with an unmigrated
/// database.
/// </para>
/// </summary>
public sealed class DatabaseMigrationHostedService : IHostedService
{
    private readonly IDatabaseMigrator _migrator;
    private readonly MigrationReadinessGate _gate;
    private readonly MigrationOptions _options;
    private readonly ILogger<DatabaseMigrationHostedService> _logger;

    public DatabaseMigrationHostedService(
        IDatabaseMigrator migrator,
        MigrationReadinessGate gate,
        MigrationOptions options,
        ILogger<DatabaseMigrationHostedService> logger)
    {
        _migrator = migrator;
        _gate = gate;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var backoff = _options.InitialBackoff;
        var attempt = 0;

        while (true)
        {
            attempt++;
            try
            {
                await _migrator.MigrateAsync(cancellationToken);
                _gate.MarkReady();
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Host is shutting down; abandon migration without failing fast.
                throw;
            }
            catch (Exception ex)
            {
                var elapsed = DateTimeOffset.UtcNow - startedAt;
                if (elapsed + backoff >= _options.MaxWait)
                {
                    // Out of retry budget: fail fast so systemd restarts us clean
                    // rather than booting half-migrated (requirement 3).
                    _logger.LogCritical(
                        ex,
                        "Database migration failed after {Attempts} attempt(s) over {Elapsed}; " +
                        "the configured database is unreachable or a migration failed. " +
                        "Exiting non-zero so the process is restarted.",
                        attempt, elapsed);
                    throw new MigrationFailedException(
                        $"Database migration did not succeed within {_options.MaxWait}.", ex);
                }

                _logger.LogWarning(
                    ex,
                    "Database migration attempt {Attempt} failed; retrying in {Backoff}.",
                    attempt, backoff);

                await Task.Delay(backoff, cancellationToken);
                backoff = NextBackoff(backoff);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private TimeSpan NextBackoff(TimeSpan current)
    {
        var next = current.TotalMilliseconds * _options.BackoffFactor;
        return TimeSpan.FromMilliseconds(Math.Min(next, _options.MaxBackoff.TotalMilliseconds));
    }
}
