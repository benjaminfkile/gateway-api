using Gateway.Api.Load;

namespace Gateway.Api.Tests;

/// <summary>
/// <see cref="IDatabaseHeadroom"/> for tests: answers <see cref="Connections"/> (null
/// for "no database") and counts the reads.
/// </summary>
public sealed class FakeDatabaseHeadroom : IDatabaseHeadroom
{
    public DatabaseConnections? Connections { get; set; } = new(MaxConnections: 400, InUse: 40);

    public int Reads { get; private set; }

    public Task<DatabaseConnections?> GetAsync(CancellationToken ct = default)
    {
        Reads++;
        return Task.FromResult(Connections);
    }
}
