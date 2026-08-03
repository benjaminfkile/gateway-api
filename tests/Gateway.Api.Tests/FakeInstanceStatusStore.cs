using Gateway.Api.Data;
using Gateway.Api.Instances;

namespace Gateway.Api.Tests;

/// <summary>
/// In-memory <see cref="IInstanceStatusStore"/> for tests: keeps the last upsert
/// per instance id and records every stale-cleanup call so tests can assert the
/// heartbeat ran and that cleanup happened on the leader only.
/// </summary>
public sealed class FakeInstanceStatusStore : IInstanceStatusStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, InstanceStatus> _rows = new(StringComparer.Ordinal);

    /// <summary>Upserts observed, in order.</summary>
    public List<InstanceStatus> Upserts { get; } = new();

    /// <summary>Cutoffs passed to <see cref="DeleteStaleAsync"/>, in order (i.e. how many times cleanup ran).</summary>
    public List<DateTimeOffset> StaleCleanups { get; } = new();

    /// <summary>Current rows keyed by instance id.</summary>
    public IReadOnlyDictionary<string, InstanceStatus> Rows
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, InstanceStatus>(_rows, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Seed a pre-existing row (e.g. a stale departed instance).</summary>
    public void Seed(InstanceStatus status)
    {
        lock (_gate)
        {
            _rows[status.InstanceId] = status;
        }
    }

    public Task UpsertAsync(InstanceStatus status, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _rows[status.InstanceId] = status;
            Upserts.Add(status);
        }

        return Task.CompletedTask;
    }

    public Task<int> DeleteStaleAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        lock (_gate)
        {
            StaleCleanups.Add(cutoff);
            var stale = _rows.Values.Where(r => r.HeartbeatAt < cutoff).Select(r => r.InstanceId).ToList();
            foreach (var id in stale)
            {
                _rows.Remove(id);
            }

            return Task.FromResult(stale.Count);
        }
    }
}

/// <summary>An <see cref="IInstanceMetadata"/> source returning a fixed identity (or declining).</summary>
public sealed class StubInstanceMetadata : IInstanceMetadata
{
    private readonly InstanceIdentity? _identity;

    public StubInstanceMetadata(InstanceIdentity? identity)
    {
        _identity = identity;
    }

    public Task<InstanceIdentity?> TryResolveAsync(CancellationToken ct = default) =>
        Task.FromResult(_identity);
}
