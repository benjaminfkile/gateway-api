using Gateway.Api.Data;

namespace Gateway.Api.Management;

/// <summary>
/// No-op <see cref="IDeployStore"/> used when no database is configured (local dev
/// without Postgres). The gateway must still boot and serve traffic with DB-backed
/// deploy history simply inactive: writes are dropped and reads return empty. The
/// Management API itself is unreachable without a Cognito authority anyway, so this
/// only ever backs the reconciler's optional deploy-progress step on a DB-less box.
/// </summary>
public sealed class NullDeployStore : IDeployStore
{
    public Task<DeployHistory> AddAsync(DeployHistory record, CancellationToken ct = default) =>
        Task.FromResult(record);

    public Task UpdateAsync(DeployHistory record, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<DeployHistory>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DeployHistory>>(Array.Empty<DeployHistory>());

    public Task<DeployHistory?> GetAsync(int id, CancellationToken ct = default) =>
        Task.FromResult<DeployHistory?>(null);

    public Task<IReadOnlyList<DeployHistory>> ListForServiceAsync(string service, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DeployHistory>>(Array.Empty<DeployHistory>());

    public Task<IReadOnlyList<DeployHistory>> ListInProgressAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DeployHistory>>(Array.Empty<DeployHistory>());

    public Task<IReadOnlyList<DeployInstanceStatus>> ListInstanceStatusesAsync(int deployId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DeployInstanceStatus>>(Array.Empty<DeployInstanceStatus>());

    public Task UpsertInstanceStatusAsync(DeployInstanceStatus status, CancellationToken ct = default) =>
        Task.CompletedTask;
}
