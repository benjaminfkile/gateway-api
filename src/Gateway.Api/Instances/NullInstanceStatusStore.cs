using Gateway.Api.Data;

namespace Gateway.Api.Instances;

/// <summary>
/// No-op <see cref="IInstanceStatusStore"/> used when no database is configured
/// (local dev without Postgres). The gateway must still boot and serve traffic
/// with DB-backed fleet awareness simply inactive, so heartbeats are silently
/// dropped rather than failing the reconcile loop.
/// </summary>
public sealed class NullInstanceStatusStore : IInstanceStatusStore
{
    public Task UpsertAsync(InstanceStatus status, CancellationToken ct = default) => Task.CompletedTask;

    public Task<int> DeleteStaleAsync(DateTimeOffset cutoff, CancellationToken ct = default) => Task.FromResult(0);
}
