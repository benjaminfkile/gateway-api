using Gateway.Api.Instances;
using Gateway.Api.Management;
using Gateway.Api.Manifest;
using Gateway.Api.Reconcile;

namespace Gateway.Api.Load;

/// <summary>
/// <see cref="ILoadPreflight"/>: loads the shared <see cref="LoadPreflightContext"/>
/// once and runs every registered check over it. Registered scoped, beside the scoped
/// stores it reads.
/// </summary>
public sealed class LoadPreflight : ILoadPreflight
{
    private readonly IEnumerable<ILoadPreflightCheck> _checks;
    private readonly ILoadStore _store;
    private readonly IManifestStore _manifests;
    private readonly IInstanceStatusStore _instances;
    private readonly LoadOptions _options;
    private readonly ReconcilerOptions _reconciler;
    private readonly TimeProvider _clock;

    public LoadPreflight(
        IEnumerable<ILoadPreflightCheck> checks,
        ILoadStore store,
        IManifestStore manifests,
        IInstanceStatusStore instances,
        LoadOptions options,
        ReconcilerOptions reconciler,
        [FromKeyedServices(LoadEndpoints.ClockKey)] TimeProvider clock)
    {
        _checks = checks;
        _store = store;
        _manifests = manifests;
        _instances = instances;
        _options = options;
        _reconciler = reconciler;
        _clock = clock;
    }

    public async Task<IReadOnlyList<PreflightCheckResult>> EvaluateAsync(
        LoadPreflightRequest request,
        CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        var scenario = string.IsNullOrWhiteSpace(request.Scenario)
            ? null
            : await _store.GetScenarioAsync(request.Scenario, ct);
        var validationRun = scenario?.LastValidationRunId is { } runId
            ? await _store.GetRunAsync(runId, ct)
            : null;
        var target = string.IsNullOrWhiteSpace(request.Target)
            ? null
            : await _manifests.GetAsync(request.Target, ct);

        var rows = await _instances.GetAllAsync(ct);
        var cutoff = now - _reconciler.InstanceStaleThreshold;

        var context = new LoadPreflightContext
        {
            Request = request,
            Options = _options,
            Now = now,
            Scenario = scenario,
            ValidationRun = validationRun,
            TargetService = target,
            FreshInstances = rows.Where(r => r.HeartbeatAt >= cutoff).OrderBy(r => r.InstanceId, StringComparer.Ordinal).ToList(),
            StaleInstances = rows.Where(r => r.HeartbeatAt < cutoff).OrderBy(r => r.InstanceId, StringComparer.Ordinal).ToList(),
            StaleThreshold = _reconciler.InstanceStaleThreshold,
        };

        var results = new List<PreflightCheckResult>();
        foreach (var check in _checks)
        {
            try
            {
                results.Add(await check.EvaluateAsync(context, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new PreflightCheckResult(
                    check.Name,
                    PreflightStatus.Red,
                    $"The check could not run: {ex.Message}",
                    "Retry; if it persists, read the gateway log."));
            }
        }

        return results;
    }
}
