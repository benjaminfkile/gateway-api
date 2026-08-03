using Gateway.Api.Data;
using Gateway.Api.Management;

namespace Gateway.Api.Tests;

/// <summary>
/// In-memory <see cref="IDeployStore"/> for reconciler tests: keeps history rows and
/// per-instance progress children so a test can assert the reconciler upserted
/// convergence and the leader marked a deploy done/partial.
/// </summary>
public sealed class FakeDeployStore : IDeployStore
{
    private readonly object _gate = new();
    private int _nextId = 1;

    public List<DeployHistory> History { get; } = new();
    public List<DeployInstanceStatus> InstanceStatuses { get; } = new();

    public Task<DeployHistory> AddAsync(DeployHistory record, CancellationToken ct = default)
    {
        lock (_gate)
        {
            record.Id = _nextId++;
            History.Add(record);
            return Task.FromResult(record);
        }
    }

    public Task UpdateAsync(DeployHistory record, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var existing = History.FirstOrDefault(h => h.Id == record.Id);
            if (existing is not null)
            {
                existing.Status = record.Status;
                existing.FinishedAt = record.FinishedAt;
                existing.Detail = record.Detail;
            }

            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<DeployHistory>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DeployHistory>>(
                History.OrderByDescending(h => h.Id).ToList());
        }
    }

    public Task<DeployHistory?> GetAsync(int id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(History.FirstOrDefault(h => h.Id == id));
        }
    }

    public Task<IReadOnlyList<DeployHistory>> ListForServiceAsync(string service, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DeployHistory>>(
                History.Where(h => h.Service == service).OrderByDescending(h => h.Id).ToList());
        }
    }

    public Task<IReadOnlyList<DeployHistory>> ListInProgressAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DeployHistory>>(
                History.Where(h => h.Status == DeployStatus.InProgress).OrderBy(h => h.Id).ToList());
        }
    }

    public Task<IReadOnlyList<DeployInstanceStatus>> ListInstanceStatusesAsync(int deployId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DeployInstanceStatus>>(
                InstanceStatuses.Where(s => s.DeployId == deployId).ToList());
        }
    }

    public Task UpsertInstanceStatusAsync(DeployInstanceStatus status, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var existing = InstanceStatuses.FirstOrDefault(
                s => s.DeployId == status.DeployId && s.InstanceId == status.InstanceId);
            if (existing is null)
            {
                InstanceStatuses.Add(status);
            }
            else
            {
                existing.Status = status.Status;
                existing.Detail = status.Detail;
                existing.UpdatedAt = status.UpdatedAt;
            }

            return Task.CompletedTask;
        }
    }
}
