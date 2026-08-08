using Gateway.Api.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Api.Tests;

/// <summary>
/// Tests automatic schema migration on boot (tech-spec §6) entirely against a fake
/// <see cref="IDatabaseMigrator"/> — no real Postgres. Covers the happy path, the
/// retry/backoff behaviour when the database is briefly unreachable, the fail-fast
/// (exit non-zero) when the retry window is exhausted, and the readiness gate the
/// reconciler blocks on.
/// </summary>
public class DatabaseMigrationTests
{
    /// <summary>Migrator that fails a fixed number of times (or forever) before succeeding.</summary>
    private sealed class FakeMigrator : IDatabaseMigrator
    {
        private int _remainingFailures;
        private readonly bool _alwaysFail;

        public FakeMigrator(int failuresBeforeSuccess = 0, bool alwaysFail = false)
        {
            _remainingFailures = failuresBeforeSuccess;
            _alwaysFail = alwaysFail;
        }

        public int Calls { get; private set; }

        public Task MigrateAsync(CancellationToken ct = default)
        {
            Calls++;
            ct.ThrowIfCancellationRequested();
            if (_alwaysFail || _remainingFailures-- > 0)
            {
                throw new InvalidOperationException("database unreachable");
            }

            return Task.CompletedTask;
        }
    }

    private static MigrationOptions FastOptions(TimeSpan? maxWait = null) => new()
    {
        InitialBackoff = TimeSpan.FromMilliseconds(1),
        MaxBackoff = TimeSpan.FromMilliseconds(2),
        BackoffFactor = 2.0,
        MaxWait = maxWait ?? TimeSpan.FromSeconds(10),
    };

    private static DatabaseMigrationHostedService Service(IDatabaseMigrator migrator, MigrationReadinessGate gate, MigrationOptions options) =>
        new(migrator, gate, options, NullLogger<DatabaseMigrationHostedService>.Instance);

    [Fact]
    public async Task Success_AppliesOnce_AndOpensGate()
    {
        var gate = new MigrationReadinessGate();
        var migrator = new FakeMigrator();
        var service = Service(migrator, gate, FastOptions());

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(1, migrator.Calls);
        Assert.True(gate.IsReady);
    }

    [Fact]
    public async Task Retries_WithBackoff_UntilSuccess()
    {
        var gate = new MigrationReadinessGate();
        var migrator = new FakeMigrator(failuresBeforeSuccess: 2);
        var service = Service(migrator, gate, FastOptions());

        await service.StartAsync(CancellationToken.None);

        // Two failures then a success: exactly three attempts, and the gate opens.
        Assert.Equal(3, migrator.Calls);
        Assert.True(gate.IsReady);
    }

    [Fact]
    public async Task FailsFast_WhenWindowExhausted_AndLeavesGateClosed()
    {
        var gate = new MigrationReadinessGate();
        var migrator = new FakeMigrator(alwaysFail: true);
        var service = Service(migrator, gate, FastOptions(maxWait: TimeSpan.FromMilliseconds(40)));

        // Bounded retry window elapses while still failing → fail fast so the
        // process exits non-zero and systemd restarts it.
        await Assert.ThrowsAsync<MigrationFailedException>(() => service.StartAsync(CancellationToken.None));

        Assert.False(gate.IsReady);
        Assert.True(migrator.Calls >= 1, "at least one migration attempt was made");
    }

    [Fact]
    public async Task Cancellation_DuringRetry_DoesNotFailFast()
    {
        var gate = new MigrationReadinessGate();
        var migrator = new FakeMigrator(alwaysFail: true);
        // Long backoff/window so cancellation, not the deadline, ends the loop.
        var service = Service(migrator, gate, new MigrationOptions
        {
            InitialBackoff = TimeSpan.FromMilliseconds(50),
            MaxBackoff = TimeSpan.FromMilliseconds(50),
            MaxWait = TimeSpan.FromSeconds(30),
        });

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(20));

        // Host shutdown surfaces as a cancellation, not a fail-fast migration error.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StartAsync(cts.Token));
        Assert.False(gate.IsReady);
    }

    [Fact]
    public async Task Gate_AlreadyReady_IsOpenImmediately()
    {
        var gate = MigrationReadinessGate.AlreadyReady();

        Assert.True(gate.IsReady);
        await gate.WaitAsync(); // completes without blocking
    }

    [Fact]
    public async Task Gate_Pending_ReleasesWaitersOnMarkReady()
    {
        var gate = new MigrationReadinessGate();
        Assert.False(gate.IsReady);

        var waiter = gate.WaitAsync();
        Assert.False(waiter.IsCompleted);

        gate.MarkReady();
        await waiter; // now completes

        Assert.True(gate.IsReady);
    }
}
