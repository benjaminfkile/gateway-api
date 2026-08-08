using Gateway.Api.Data;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Gateway.Api.Instances;

/// <summary>
/// Production <see cref="ILeaderLockGateway"/> over Npgsql (tech-spec §4.3). Holds
/// the session advisory lock on one dedicated connection, and runs lease
/// reads/writes and <c>pg_terminate_backend</c> fencing on short-lived connections
/// so they never disturb the lock-holding session. Only wired when a Postgres
/// connection string is configured; tests use a fake gateway.
/// </summary>
internal sealed class NpgsqlLeaderLockGateway : ILeaderLockGateway
{
    private readonly string _connectionString;
    private readonly long _lockKey;
    private readonly ILogger? _logger;

    // pg_locks splits a single bigint advisory key into two int4 halves plus an
    // objsubid discriminator (1 = the single-bigint form of pg_advisory_lock).
    private readonly int _classId;
    private readonly int _objId;

    private NpgsqlConnection? _lockConnection;

    public NpgsqlLeaderLockGateway(string connectionString, long lockKey, ILogger? logger = null)
    {
        _connectionString = connectionString;
        _lockKey = lockKey;
        _logger = logger;
        _classId = unchecked((int)(lockKey >> 32));
        _objId = unchecked((int)(lockKey & 0xFFFFFFFF));
    }

    public async Task<bool> TryAcquireLockAsync(CancellationToken ct)
    {
        _lockConnection ??= await OpenAsync(ct);

        await using var cmd = _lockConnection.CreateCommand();
        cmd.CommandText = "SELECT pg_try_advisory_lock(@key)";
        cmd.Parameters.AddWithValue("key", _lockKey);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is bool acquired && acquired;
    }

    public async Task<bool> IsLockConnectionAliveAsync(CancellationToken ct)
    {
        if (_lockConnection is not { State: System.Data.ConnectionState.Open })
        {
            return false;
        }

        try
        {
            await using var ping = _lockConnection.CreateCommand();
            ping.CommandText = "SELECT 1";
            await ping.ExecuteScalarAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    public async Task ReleaseLockConnectionAsync()
    {
        if (_lockConnection is not null)
        {
            try
            {
                // Closing the session releases every advisory lock it holds.
                await _lockConnection.DisposeAsync();
            }
            catch
            {
                // Best-effort teardown of a possibly-broken connection.
            }

            _lockConnection = null;
        }
    }

    public async Task<LeaseSnapshot?> GetLeaseAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT holder_instance_id, acquired_at, renewed_at FROM leader_lease WHERE id = @id";
        cmd.Parameters.AddWithValue("id", LeaderLease.SingletonId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new LeaseSnapshot(
            reader.GetString(0),
            reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetFieldValue<DateTimeOffset>(2));
    }

    public async Task UpsertLeaseAsync(string instanceId, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // Keep acquired_at stable while the same instance renews; reset it when the
        // holder changes (a fresh acquisition, including after fencing).
        cmd.CommandText = """
            INSERT INTO leader_lease (id, holder_instance_id, acquired_at, renewed_at)
            VALUES (@id, @holder, @now, @now)
            ON CONFLICT (id) DO UPDATE SET
                holder_instance_id = EXCLUDED.holder_instance_id,
                acquired_at = CASE
                    WHEN leader_lease.holder_instance_id = EXCLUDED.holder_instance_id
                    THEN leader_lease.acquired_at ELSE EXCLUDED.acquired_at END,
                renewed_at = EXCLUDED.renewed_at
            """;
        cmd.Parameters.AddWithValue("id", LeaderLease.SingletonId);
        cmd.Parameters.AddWithValue("holder", instanceId);
        cmd.Parameters.AddWithValue("now", now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RenewLeaseAsync(string instanceId, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE leader_lease SET renewed_at = @now WHERE id = @id AND holder_instance_id = @holder";
        cmd.Parameters.AddWithValue("id", LeaderLease.SingletonId);
        cmd.Parameters.AddWithValue("holder", instanceId);
        cmd.Parameters.AddWithValue("now", now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ClearLeaseAsync(string instanceId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM leader_lease WHERE id = @id AND holder_instance_id = @holder";
        cmd.Parameters.AddWithValue("id", LeaderLease.SingletonId);
        cmd.Parameters.AddWithValue("holder", instanceId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int?> FenceLockHolderAsync(CancellationToken ct)
    {
        // Terminate whichever backend holds the fleet advisory lock. Runs on a fresh
        // connection (never the dead holder's, never our own lock session). Same
        // login role owning the target session is enough for pg_terminate_backend —
        // no superuser required (tech-spec §4.3). Exclude our own backend defensively.
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT l.pid, pg_terminate_backend(l.pid)
            FROM pg_locks l
            WHERE l.locktype = 'advisory'
              AND l.classid = @classid::oid
              AND l.objid = @objid::oid
              AND l.objsubid = 1
              AND l.granted
              AND l.pid <> pg_backend_pid()
            """;
        cmd.Parameters.AddWithValue("classid", _classId);
        cmd.Parameters.AddWithValue("objid", _objId);

        int? fencedPid = null;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            fencedPid ??= reader.GetInt32(0);
        }

        return fencedPid;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        await ReleaseLockConnectionAsync();
    }
}
