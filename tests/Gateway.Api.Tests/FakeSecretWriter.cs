using Gateway.Api.Reconcile;

namespace Gateway.Api.Tests;

/// <summary>
/// In-memory <see cref="ISecretWriter"/> for tests: records every write in order and
/// keeps the newest value per reference. <see cref="Error"/>, when set, is thrown by
/// every write instead.
/// </summary>
public sealed class FakeSecretWriter : ISecretWriter
{
    private readonly object _gate = new();
    private readonly List<(string Reference, string Value)> _writes = new();

    public Exception? Error { get; set; }

    public IReadOnlyList<(string Reference, string Value)> Writes
    {
        get
        {
            lock (_gate)
            {
                return _writes.ToList();
            }
        }
    }

    public Task PutSecretStringAsync(string reference, string value, CancellationToken ct = default)
    {
        if (Error is { } error)
        {
            throw error;
        }

        lock (_gate)
        {
            _writes.Add((reference, value));
        }

        return Task.CompletedTask;
    }
}
