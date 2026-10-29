using Gateway.Api.Data;

namespace Gateway.Api.Management;

/// <summary>
/// No-op <see cref="ILoadStore"/> used when no database is configured (local dev
/// without Postgres), so the load endpoints still bind. Writes are dropped, reads
/// return empty, and the conditional updates (claim, check-in, stop, verdict, delete)
/// match nothing; the Management API is unreachable without a Cognito authority
/// anyway, the same as <see cref="NullJobStore"/>.
/// </summary>
public sealed class NullLoadStore : ILoadStore
{
    public Task<IReadOnlyList<LoadScenario>> ListScenariosAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LoadScenario>>(Array.Empty<LoadScenario>());

    public Task<LoadScenario?> GetScenarioAsync(string name, CancellationToken ct = default) =>
        Task.FromResult<LoadScenario?>(null);

    public Task UpsertScenarioAsync(LoadScenario scenario, LoadScenarioVersion? version = null, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeleteScenarioAsync(string name, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<LoadScenarioVersion>> ListScenarioVersionsAsync(string scenario, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LoadScenarioVersion>>(Array.Empty<LoadScenarioVersion>());

    public Task<LoadScenarioVersion?> GetScenarioVersionAsync(string scenario, int version, CancellationToken ct = default) =>
        Task.FromResult<LoadScenarioVersion?>(null);

    public Task<LoadRun?> TryCreateRunAsync(LoadRun run, CancellationToken ct = default) =>
        Task.FromResult<LoadRun?>(run);

    public Task<LoadRun?> GetActiveRunAsync(CancellationToken ct = default) =>
        Task.FromResult<LoadRun?>(null);

    public Task<LoadRun?> GetActiveRunForScenarioAsync(string scenario, CancellationToken ct = default) =>
        Task.FromResult<LoadRun?>(null);

    public Task<LoadRun?> GetRunAsync(long id, CancellationToken ct = default) =>
        Task.FromResult<LoadRun?>(null);

    public Task<IReadOnlyList<LoadRun>> ListRunsAsync(int limit, long? before, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LoadRun>>(Array.Empty<LoadRun>());

    public Task<LoadRun?> ClaimQueuedRunAsync(long id, string by, DateTimeOffset now, CancellationToken ct = default) =>
        Task.FromResult<LoadRun?>(null);

    public Task<LoadCheckInResult> CheckInAsync(long runId, string instanceId, CancellationToken ct = default) =>
        Task.FromResult(new LoadCheckInResult(LoadCheckInOutcome.NotFound, -1, null));

    public Task<LoadRun?> RequestStopAsync(long id, string by, DateTimeOffset now, CancellationToken ct = default) =>
        Task.FromResult<LoadRun?>(null);

    public Task<bool> SetVerdictAsync(long id, string? verdict, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<bool> DeleteRunAsync(long id, CancellationToken ct = default) => Task.FromResult(false);

    public Task<LoadRunStat> AppendStatAsync(LoadRunStat stat, CancellationToken ct = default) =>
        Task.FromResult(stat);

    public Task UpdatePeaksAsync(long runId, IReadOnlyDictionary<string, double> sample, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<LoadRunStat>> ListStatsAsync(
        long runId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? source,
        int? index,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LoadRunStat>>(Array.Empty<LoadRunStat>());

    public Task<IReadOnlyList<LoadRunStat>> GetLatestGeneratorStatsAsync(long runId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LoadRunStat>>(Array.Empty<LoadRunStat>());
}
