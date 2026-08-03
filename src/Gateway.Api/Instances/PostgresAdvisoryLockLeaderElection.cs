using Microsoft.Extensions.Logging;
using Npgsql;

namespace Gateway.Api.Instances;

/// <summary>
/// Postgres-advisory-lock leader election (tech-spec §3, §4.3). Leadership is a
/// session-level <c>pg_try_advisory_lock</c> on a fixed key, held for as long as a
/// <b>dedicated</b> connection stays open — so if the leader process dies (or its
/// connection drops), Postgres releases the lock automatically and another
/// instance can claim it on its next loop.
/// <para>
/// Each call re-verifies leadership: it makes sure the dedicated connection is
/// still alive and, if leadership is not currently held, re-attempts the lock.
/// The build/test box has no Postgres, so this type is only wired in production
/// (a DB connection string is configured); tests use
/// <see cref="InMemoryLeaderElection"/>.
/// </para>
/// </summary>
public sealed class PostgresAdvisoryLockLeaderElection : ILeaderElection, IAsyncDisposable
{
    /// <summary>
    /// Fixed advisory-lock key identifying "the gateway fleet leader". Arbitrary but
    /// stable — every instance in the fleet contends on this same key.
    /// </summary>
    public const long DefaultLockKey = 0x6761_7465_7761_79L; // "gateway" in hex-ish

    private readonly string _connectionString;
    private readonly long _lockKey;
    private readonly ILogger<PostgresAdvisoryLockLeaderElection>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private NpgsqlConnection? _connection;
    private bool _held;

    public PostgresAdvisoryLockLeaderElection(
        string connectionString,
        long lockKey = DefaultLockKey,
        ILogger<PostgresAdvisoryLockLeaderElection>? logger = null)
    {
        _connectionString = connectionString;
        _lockKey = lockKey;
        _logger = logger;
    }

    public async Task<bool> TryAcquireAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Re-verify each loop. The lock is session-scoped, so if we believe we
            // hold it, confirm the dedicated connection is still alive with a cheap
            // ping — a dropped connection means Postgres already released the lock
            // and another instance may have taken over. We must NOT re-run
            // pg_try_advisory_lock on a session that already holds it (it stacks and
            // would need matching unlocks); the ping is the re-verification.
            if (_held && _connection is { State: System.Data.ConnectionState.Open })
            {
                if (await IsConnectionAliveAsync(ct))
                {
                    return true;
                }

                // Connection is dead — we lost leadership. Reset and re-contend below.
                await ResetConnectionAsync();
            }
            else if (_connection is not { State: System.Data.ConnectionState.Open })
            {
                await ResetConnectionAsync();
            }

            _connection ??= await OpenConnectionAsync(ct);

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT pg_try_advisory_lock(@key)";
            cmd.Parameters.AddWithValue("key", _lockKey);

            var result = await cmd.ExecuteScalarAsync(ct);
            _held = result is bool acquired && acquired;

            if (_held)
            {
                _logger?.LogInformation("Acquired fleet leader advisory lock (key {Key}).", _lockKey);
            }

            return _held;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Any DB error means we cannot claim leadership this loop; fail closed
            // (not leader) and reset so the next loop reconnects cleanly.
            _logger?.LogWarning(ex, "Leader-election lock attempt failed; treating as non-leader.");
            await ResetConnectionAsync();
            _held = false;
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> IsConnectionAliveAsync(CancellationToken ct)
    {
        try
        {
            await using var ping = _connection!.CreateCommand();
            ping.CommandText = "SELECT 1";
            await ping.ExecuteScalarAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private async Task ResetConnectionAsync()
    {
        if (_connection is not null)
        {
            try
            {
                await _connection.DisposeAsync();
            }
            catch
            {
                // Best-effort teardown of a broken connection.
            }

            _connection = null;
        }

        _held = false;
    }

    public async ValueTask DisposeAsync()
    {
        await ResetConnectionAsync();
        _gate.Dispose();
    }
}
