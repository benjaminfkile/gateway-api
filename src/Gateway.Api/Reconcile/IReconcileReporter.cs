using Microsoft.Extensions.Logging;

namespace Gateway.Api.Reconcile;

/// <summary>The result of executing one reconcile action.</summary>
public enum ReconcileOutcomeStatus
{
    /// <summary>The action completed successfully.</summary>
    Succeeded,

    /// <summary>The action failed and was aborted cleanly, leaving prior state intact.</summary>
    Failed,
}

/// <summary>
/// A record of one reconcile action's outcome (tech-spec §7: "record outcome").
/// A later task persists these to <c>deploy_history</c> / <c>deploy_instance_status</c>;
/// for now they are logged and are observable in tests.
/// </summary>
/// <param name="ServiceName">The service the action concerned.</param>
/// <param name="Kind">The action that ran.</param>
/// <param name="Status">Whether it succeeded or was aborted.</param>
/// <param name="Detail">Human-readable detail (drift reason, failure cause, …).</param>
public sealed record ReconcileOutcome(
    string ServiceName,
    ReconcileActionKind Kind,
    ReconcileOutcomeStatus Status,
    string Detail);

/// <summary>
/// Sink for reconcile outcomes. Behind an interface so tests can capture them and
/// a later task can persist them to the deploy-history tables.
/// </summary>
public interface IReconcileReporter
{
    Task ReportAsync(ReconcileOutcome outcome, CancellationToken ct = default);
}

/// <summary>Default reporter: writes a structured log line per outcome.</summary>
public sealed class LoggingReconcileReporter : IReconcileReporter
{
    private readonly ILogger<LoggingReconcileReporter> _logger;

    public LoggingReconcileReporter(ILogger<LoggingReconcileReporter> logger)
    {
        _logger = logger;
    }

    public Task ReportAsync(ReconcileOutcome outcome, CancellationToken ct = default)
    {
        if (outcome.Status == ReconcileOutcomeStatus.Succeeded)
        {
            _logger.LogInformation("Reconcile {Kind} for {Service} succeeded: {Detail}",
                outcome.Kind, outcome.ServiceName, outcome.Detail);
        }
        else
        {
            _logger.LogWarning("Reconcile {Kind} for {Service} failed: {Detail}",
                outcome.Kind, outcome.ServiceName, outcome.Detail);
        }

        return Task.CompletedTask;
    }
}
