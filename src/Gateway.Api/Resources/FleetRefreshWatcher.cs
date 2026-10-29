using Gateway.Api.Data;
using Gateway.Api.Instances;
using Gateway.Api.Management;
using Gateway.Api.RealTime;
using Gateway.Api.Reconcile;
using Microsoft.Extensions.Hosting;

namespace Gateway.Api.Resources;

/// <summary>
/// Follows the fleet instance refreshes the gateway started (docs/load.md section 9).
/// Every <see cref="PollInterval"/> it reads the newest <c>fleet</c> audit rows, takes
/// each class change or rollback whose detail names a refresh with no
/// <c>refresh_ended</c> row yet, reads that refresh through
/// <see cref="IFleetScaler.DescribeInstanceRefreshesAsync"/>, and publishes
/// <c>refresh</c> on <c>ops:resources</c>. When the refresh has ended (any terminal
/// status, or Auto Scaling no longer knows it) it puts the group's max back to the
/// value the refresh start raised it from and writes the <c>refresh_ended</c> row, so
/// the max is restored on success, failure and cancel alike.
/// <para>
/// The pending refresh lives in the database rather than in this process because the
/// refresh replaces the fleet, the instance that started it included. Only the fresh
/// leader acts, so one instance restores; an instance with the reconciler disabled has
/// no election and acts alone.
/// </para>
/// </summary>
public sealed class FleetRefreshWatcher : BackgroundService
{
    /// <summary>How often a refresh in flight is polled.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>The actor of the <c>refresh_ended</c> rows the watcher writes.</summary>
    public const string Actor = "gateway";

    /// <summary>How many of the newest fleet audit rows are searched for a pending refresh.</summary>
    public const int AuditWindow = 100;

    private readonly IServiceScopeFactory _scopes;
    private readonly ILeaderStatus _leader;
    private readonly ReconcilerOptions _reconciler;
    private readonly IChannelEventPublisher _publisher;
    private readonly TimeProvider _clock;
    private readonly ILogger<FleetRefreshWatcher> _logger;
    private readonly TimeSpan _interval;
    private readonly MigrationReadinessGate? _migrationGate;

    public FleetRefreshWatcher(
        IServiceScopeFactory scopes,
        ILeaderStatus leader,
        ReconcilerOptions reconciler,
        IChannelEventPublisher publisher,
        [FromKeyedServices(ResourceEndpoints.ClockKey)] TimeProvider clock,
        ILogger<FleetRefreshWatcher> logger,
        MigrationReadinessGate migrationGate)
        : this(scopes, leader, reconciler, publisher, clock, logger, PollInterval, migrationGate)
    {
    }

    public FleetRefreshWatcher(
        IServiceScopeFactory scopes,
        ILeaderStatus leader,
        ReconcilerOptions reconciler,
        IChannelEventPublisher publisher,
        TimeProvider clock,
        ILogger<FleetRefreshWatcher> logger,
        TimeSpan interval,
        MigrationReadinessGate? migrationGate = null)
    {
        _scopes = scopes;
        _leader = leader;
        _reconciler = reconciler;
        _publisher = publisher;
        _clock = clock;
        _logger = logger;
        _interval = interval;
        _migrationGate = migrationGate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The audit table must exist before the first read.
        if (_migrationGate is not null)
        {
            try
            {
                await _migrationGate.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, _clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Fleet refresh watcher tick failed; retrying next tick.");
            }
        }
    }

    /// <summary>One poll: publish progress of every pending refresh and close the ended ones.</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        if (!ShouldAct())
        {
            return;
        }

        using var scope = _scopes.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IResourceAuditStore>();
        var scaler = scope.ServiceProvider.GetRequiredService<IFleetScaler>();

        foreach (var (row, pending) in await PendingAsync(audit, ct))
        {
            try
            {
                await FollowAsync(audit, scaler, pending, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not follow fleet refresh {RefreshId} of audit row {Row}.", pending.RefreshId, row.Id);
            }
        }
    }

    private bool ShouldAct()
    {
        if (!_reconciler.Enabled)
        {
            return true;
        }

        return _leader.Current.IsLeader
            && _leader.EvaluatedAt is { } at
            && _clock.GetUtcNow() - at < _reconciler.InstanceStaleThreshold;
    }

    private static async Task<IReadOnlyList<(ResourceAudit Row, ResourceEndpoints.RefreshDetail Detail)>> PendingAsync(
        IResourceAuditStore audit,
        CancellationToken ct)
    {
        var rows = await audit.ListAsync(AuditWindow, ResourceEndpoints.FleetResource, ct);

        var ended = rows
            .Where(r => r.Action == ResourceEndpoints.RefreshEndedAction)
            .Select(ResourceEndpoints.ReadRefreshDetail)
            .Where(d => d is not null)
            .Select(d => d!.RefreshId)
            .ToHashSet(StringComparer.Ordinal);

        return rows
            .Where(r => r.Action is ResourceEndpoints.ClassAction or ResourceEndpoints.ClassRollbackAction)
            .Select(r => (Row: r, Detail: ResourceEndpoints.ReadRefreshDetail(r)))
            .Where(p => p.Detail is not null && !ended.Contains(p.Detail.RefreshId))
            .Select(p => (p.Row, p.Detail!))
            .Reverse()
            .ToList();
    }

    private async Task FollowAsync(IResourceAuditStore audit, IFleetScaler scaler, ResourceEndpoints.RefreshDetail pending, CancellationToken ct)
    {
        var refresh = (await scaler.DescribeInstanceRefreshesAsync(pending.Group, pending.RefreshId, ct))
            .FirstOrDefault(r => string.Equals(r.RefreshId, pending.RefreshId, StringComparison.Ordinal));
        var status = refresh?.Status ?? "NotFound";

        _publisher.TryPublish(ResourceEndpoints.OpsResourcesChannel, "refresh", new
        {
            refreshId = pending.RefreshId,
            status,
            percentComplete = refresh?.PercentComplete,
        });

        if (refresh is { IsTerminal: false })
        {
            return;
        }

        // Ended: put the max back. Never below what min and desired need, which the
        // refresh does not change but an operator outside the gateway might have.
        var size = await scaler.DescribeGroupAsync(pending.Group, ct);
        string? before = null;
        var restored = pending.RestoreMax;
        if (size is not null)
        {
            restored = Math.Max(pending.RestoreMax, Math.Max(size.Min, size.Desired));
            before = $"max {size.Max}";
            if (size.Max != restored)
            {
                await scaler.SetGroupSizeAsync(pending.Group, size.Min, size.Desired, restored, ct);
            }
        }

        await ResourceEndpoints.RecordAsync(audit, _publisher, _clock, null, ResourceEndpoints.FleetResource,
            ResourceEndpoints.RefreshEndedAction, before, $"max {restored}",
            new { group = pending.Group, refreshId = pending.RefreshId, status, restoredMax = restored }, ct);

        _logger.LogInformation(
            "Fleet refresh {RefreshId} ended {Status}; group {Group} max restored to {Max}.",
            pending.RefreshId, status, pending.Group, restored);
    }
}
