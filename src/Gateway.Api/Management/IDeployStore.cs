using Gateway.Api.Data;

namespace Gateway.Api.Management;

/// <summary>
/// Read/write access to the deploy-history tables (tech-spec §4.4): the
/// <c>deploy_history</c> audit/rollout ledger and its <c>deploy_instance_status</c>
/// per-instance progress children. The Management API writes a row for every
/// mutating action and reads history back for the dashboard; the reconciler upserts
/// per-instance progress and the leader marks a deploy complete once every live
/// instance has converged (tech-spec §7).
/// <para>
/// Two implementations exist: <see cref="EfDeployStore"/> over Postgres
/// (production) and <see cref="NullDeployStore"/> for a box with no database
/// (local dev / tests use a Sqlite-backed <see cref="EfDeployStore"/> or a fake).
/// </para>
/// </summary>
public interface IDeployStore
{
    /// <summary>Insert a history row and return it with its generated <see cref="DeployHistory.Id"/>.</summary>
    Task<DeployHistory> AddAsync(DeployHistory record, CancellationToken ct = default);

    /// <summary>Update an existing history row (e.g. leader marking it done/partial).</summary>
    Task UpdateAsync(DeployHistory record, CancellationToken ct = default);

    /// <summary>All history rows, most recent first.</summary>
    Task<IReadOnlyList<DeployHistory>> ListAsync(CancellationToken ct = default);

    /// <summary>A single history row by id, or null when absent.</summary>
    Task<DeployHistory?> GetAsync(int id, CancellationToken ct = default);

    /// <summary>History rows for one service, most recent first.</summary>
    Task<IReadOnlyList<DeployHistory>> ListForServiceAsync(string service, CancellationToken ct = default);

    /// <summary>History rows still <c>in_progress</c> (the reconciler drives these to completion).</summary>
    Task<IReadOnlyList<DeployHistory>> ListInProgressAsync(CancellationToken ct = default);

    /// <summary>Per-instance progress children for a deploy.</summary>
    Task<IReadOnlyList<DeployInstanceStatus>> ListInstanceStatusesAsync(int deployId, CancellationToken ct = default);

    /// <summary>Insert or update one per-instance progress row (keyed by deploy + instance).</summary>
    Task UpsertInstanceStatusAsync(DeployInstanceStatus status, CancellationToken ct = default);
}
