using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gateway.Api.Data;
using Gateway.Api.Instances;
using Gateway.Api.Management;
using Gateway.Api.RealTime;
using Gateway.Api.Reconcile;
using Gateway.Api.Resources;

namespace Gateway.Api.Load;

/// <summary>
/// The load scheduler (docs/load.md section 3). Ticks every
/// <see cref="LoadOptions.TickInterval"/> and acts only while <see cref="ILeaderStatus"/>
/// says this instance is the leader and the evaluation is fresher than the heartbeat
/// stale threshold, like the job scheduler. Each tick, in order:
/// <list type="number">
/// <item>Guard: with no run non-terminal, a generator group whose desired capacity is
/// above 0 is set to 0 and an <c>orphan_guard</c> resource audit row is written.</item>
/// <item>Claim the queued run (one load run at a time, so there is at most one) with
/// the store's conditional claim, then run pre-flight again; a red check fails it.</item>
/// <item>Gaining leadership: a non-terminal run this process is not driving is adopted
/// when it is in <c>fleet_up</c> or <c>generators_up</c> (both are state checks any
/// leader can continue; a fleet grown by the run itself hands leadership to a new node
/// whenever that node's instance id sorts lower), when it is ramping or holding with
/// fresh generator stats, or already draining; any other goes to <c>draining</c> with
/// reason <c>leader_restart</c>.</item>
/// <item>Drive the run one step through the state table: the exits (hard clock, stale
/// stats, operator stop) first, then the step of its status.</item>
/// <item>Write a fleet sample every <see cref="LoadOptions.FleetSampleInterval"/>.</item>
/// </list>
/// Every status change is one <see cref="ILoadStore.TryAdvanceRunAsync"/> with a
/// timeline entry, followed by a <c>run</c> event on <c>ops:load</c>. Every path into
/// <c>draining</c> sets the generator group's desired capacity to 0 first. A step whose
/// scaler or secret call throws is retried on the next tick; after
/// <see cref="LoadOptions.ScaleRetryLimit"/> failed ticks in a row the run fails, with
/// the generator group set to 0 whether or not that call succeeds.
/// </summary>
public sealed class LoadSchedulerService : BackgroundService
{
    /// <summary>The <c>by</c> of the timeline entries and audit rows the scheduler writes.</summary>
    public const string Actor = "gateway";

    /// <summary>The <c>resource_audit.resource</c> of the generator group.</summary>
    public const string GeneratorsResource = "generators";

    /// <summary>The <c>resource_audit.action</c> of the orphan guard.</summary>
    public const string OrphanGuardAction = "orphan_guard";

    /// <summary>Detail of a run whose generators did not all check in within <see cref="LoadOptions.CheckInTimeout"/>.</summary>
    public const string CheckInTimeoutDetail = "checkin_timeout";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFleetScaler _scaler;
    private readonly ISecretWriter _secrets;
    private readonly FleetGroupResolver _fleetGroup;
    private readonly ILeaderStatus _leader;
    private readonly LoadOptions _options;
    private readonly ReconcilerOptions _reconciler;
    private readonly ILoadMetrics _metrics;
    private readonly ILogger<LoadSchedulerService> _logger;
    private readonly TimeProvider _time;
    private readonly MigrationReadinessGate? _migrationGate;
    private readonly IChannelEventPublisher? _publisher;
    private readonly IPresenceRegistry? _presence;

    // Runs this process drives: claimed here, or adopted on gaining leadership. Cleared
    // whenever this instance is not the fresh leader, so the next leading tick counts
    // as gaining leadership.
    private readonly ConcurrentDictionary<long, byte> _driven = new();

    // Consecutive failed ticks per run.
    private readonly ConcurrentDictionary<long, int> _failures = new();

    // When this process last wrote a fleet sample per run.
    private readonly ConcurrentDictionary<long, DateTimeOffset> _sampledAt = new();

    public LoadSchedulerService(
        IServiceScopeFactory scopeFactory,
        IFleetScaler scaler,
        ISecretWriter secrets,
        FleetGroupResolver fleetGroup,
        ILeaderStatus leader,
        LoadOptions options,
        ReconcilerOptions reconciler,
        ILoadMetrics metrics,
        ILogger<LoadSchedulerService> logger,
        TimeProvider? time = null,
        MigrationReadinessGate? migrationGate = null,
        IChannelEventPublisher? publisher = null,
        IPresenceRegistry? presence = null)
    {
        _scopeFactory = scopeFactory;
        _scaler = scaler;
        _secrets = secrets;
        _fleetGroup = fleetGroup;
        _leader = leader;
        _options = options;
        _reconciler = reconciler;
        _metrics = metrics;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _migrationGate = migrationGate;
        _publisher = publisher;
        _presence = presence;
    }

    /// <summary>Ids of the runs this process drives right now.</summary>
    public IReadOnlyCollection<long> DrivenRunIds => _driven.Keys.ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

        _logger.LogInformation("Load scheduler running every {Interval}.", _options.TickInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Load scheduler tick failed");
            }

            try
            {
                await Task.Delay(_options.TickInterval, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One tick (see the class summary). Public so tests drive ticks directly. Nothing
    /// happens unless this instance is the fresh leader and <c>Load:GeneratorGroup</c>
    /// is set.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        if (!IsFreshLeader(now))
        {
            _driven.Clear();
            _failures.Clear();
            return;
        }

        if (!_options.IsConfigured)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ILoadStore>();

        // At most one run is non-terminal (TryCreateRunAsync and ux_load_run_active).
        var run = await store.GetActiveRunAsync(ct);
        if (run is null)
        {
            _sampledAt.Clear();
            await GuardStepAsync("guard the generator group", () => OrphanGuardAsync(scope.ServiceProvider, ct));
            return;
        }

        if (run.Status == LoadRunStatus.Queued)
        {
            await StepAsync(run, () => ClaimAsync(scope.ServiceProvider, store, run, now, ct));
        }
        else if (!_driven.ContainsKey(run.Id))
        {
            await StepAsync(run, () => AdoptOrDrainAsync(store, run, now, ct));
        }
        else
        {
            await StepAsync(run, () => DriveAsync(scope.ServiceProvider, store, run, now, ct));
        }

        await GuardStepAsync("write a fleet sample", () => SampleFleetAsync(scope.ServiceProvider, store, run.Id, now, ct));
    }

    private bool IsFreshLeader(DateTimeOffset now) =>
        _leader.Current.IsLeader
        && _leader.EvaluatedAt is { } at
        && now - at < _reconciler.InstanceStaleThreshold;

    private async Task GuardStepAsync(string step, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Load scheduler failed to {Step}; retrying next tick.", step);
        }
    }

    /// <summary>
    /// Run one step of <paramref name="run"/>. A throw counts as a failed tick; the
    /// <see cref="LoadOptions.ScaleRetryLimit"/>th in a row fails the run. A step that
    /// completes resets the count.
    /// </summary>
    private async Task StepAsync(LoadRun run, Func<Task> step)
    {
        try
        {
            await step();
            _failures.TryRemove(run.Id, out _);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failures = _failures.AddOrUpdate(run.Id, 1, (_, n) => n + 1);
            _logger.LogWarning(
                "Load run {RunId} step in {Status} failed ({Failures} of {Limit}): {Error}",
                run.Id, run.Status, failures, _options.ScaleRetryLimit, ex.Message);
            if (failures < _options.ScaleRetryLimit)
            {
                return;
            }

            _failures.TryRemove(run.Id, out _);
            await GuardStepAsync("fail the run after the retry limit", () => FailAfterRetriesAsync(run.Id, ex));
        }
    }

    // ---------- guard and claim ----------

    private async Task OrphanGuardAsync(IServiceProvider services, CancellationToken ct)
    {
        var group = _options.GeneratorGroup!;
        var size = await _scaler.DescribeGroupAsync(group, ct);
        if (size is null || size.Desired <= 0)
        {
            return;
        }

        await _scaler.SetDesiredAsync(group, 0, ct);
        _logger.LogWarning(
            "No load run is active but generator group {Group} had desired {Desired}; set it to 0.", group, size.Desired);

        await ResourceEndpoints.RecordAsync(
            services.GetRequiredService<IResourceAuditStore>(),
            _publisher ?? NoPublisher.Instance,
            _time,
            user: null,
            GeneratorsResource,
            OrphanGuardAction,
            string.Create(CultureInfo.InvariantCulture, $"desired {size.Desired}, in service {size.InService}"),
            "desired 0",
            new { group, reason = "no load run is non-terminal" },
            ct);
    }

    private async Task ClaimAsync(IServiceProvider services, ILoadStore store, LoadRun run, DateTimeOffset now, CancellationToken ct)
    {
        var claimed = await store.ClaimQueuedRunAsync(run.Id, Actor, now, ct);
        if (claimed is null)
        {
            return;
        }

        _driven[claimed.Id] = 0;
        _logger.LogInformation("Claimed load run {RunId} of scenario {Scenario}.", claimed.Id, claimed.Scenario);
        Broadcast(claimed);

        // Pre-flight again: the fleet may have changed since Start.
        var checks = await services.GetRequiredService<ILoadPreflight>().EvaluateAsync(new LoadPreflightRequest(
            claimed.Scenario,
            claimed.Target,
            claimed.Target,
            claimed.Generators,
            claimed.VusPerGenerator,
            claimed.FleetSizeRequested,
            claimed.Validation), ct);
        var red = checks.FirstOrDefault(c => c.Status == PreflightStatus.Red);
        if (red is not null)
        {
            _logger.LogWarning("Load run {RunId} failed pre-flight at claim: {Check}.", claimed.Id, red.Name);
            await DrainAsync(store, claimed, LoadStopReason.Failed, red.Name, now, ct);
        }
    }

    private async Task AdoptOrDrainAsync(ILoadStore store, LoadRun run, DateTimeOffset now, CancellationToken ct)
    {
        var adopt = run.Status is LoadRunStatus.Draining or LoadRunStatus.FleetUp or LoadRunStatus.GeneratorsUp
            || (run.Status is LoadRunStatus.Ramping or LoadRunStatus.Holding && !await StatsStaleAsync(store, run, now, ct));
        if (adopt)
        {
            _driven[run.Id] = 0;
            _logger.LogInformation("Adopted load run {RunId} in {Status} on gaining leadership.", run.Id, run.Status);
            return;
        }

        _logger.LogWarning(
            "Load run {RunId} in {Status} has no driver on this leader and cannot be re-attached; draining it.", run.Id, run.Status);
        if (await DrainAsync(store, run, LoadStopReason.LeaderRestart, null, now, ct) is not null)
        {
            _driven[run.Id] = 0;
        }
    }

    // ---------- the state table ----------

    private async Task DriveAsync(IServiceProvider services, ILoadStore store, LoadRun run, DateTimeOffset now, CancellationToken ct)
    {
        if (run.Status is LoadRunStatus.FleetUp or LoadRunStatus.GeneratorsUp or LoadRunStatus.Ramping or LoadRunStatus.Holding)
        {
            var reason = await ExitReasonAsync(store, run, now, ct);
            if (reason is not null)
            {
                await DrainAsync(store, run, reason, null, now, ct);
                return;
            }
        }

        switch (run.Status)
        {
            case LoadRunStatus.FleetUp:
                await FleetUpAsync(services, store, run, now, ct);
                break;

            case LoadRunStatus.GeneratorsUp:
                await GeneratorsUpAsync(store, run, now, ct);
                break;

            case LoadRunStatus.Ramping:
                if (now >= (run.RampStartedAt ?? now) + TimeSpan.FromSeconds(run.RampSeconds))
                {
                    await AdvanceAsync(store, run, r => r.Status = LoadRunStatus.Holding, now, null, ct);
                }

                break;

            case LoadRunStatus.Holding:
                if (now >= EnteredAt(run) + TimeSpan.FromSeconds(run.HoldSeconds))
                {
                    await DrainAsync(store, run, null, null, now, ct);
                }

                break;

            case LoadRunStatus.Draining:
                await DrainingAsync(store, run, now, ct);
                break;
        }
    }

    /// <summary>
    /// Why a run must leave for <c>draining</c> now, or null: past
    /// <c>hard_stop_at</c> (<c>hard_clock</c>), no generator line for
    /// <see cref="LoadOptions.StatsStaleAfter"/> while ramping or holding (<c>lost</c>),
    /// or a stop request (<c>operator</c>).
    /// </summary>
    private async Task<string?> ExitReasonAsync(ILoadStore store, LoadRun run, DateTimeOffset now, CancellationToken ct)
    {
        if (now >= run.HardStopAt)
        {
            return LoadStopReason.HardClock;
        }

        if (run.Status is LoadRunStatus.Ramping or LoadRunStatus.Holding && await StatsStaleAsync(store, run, now, ct))
        {
            return LoadStopReason.Lost;
        }

        return run.StopRequestedAt is not null ? LoadStopReason.Operator : null;
    }

    /// <summary>
    /// True when the newest generator line (or the ramp start, when no line is newer)
    /// is older than <see cref="LoadOptions.StatsStaleAfter"/>.
    /// </summary>
    private async Task<bool> StatsStaleAsync(ILoadStore store, LoadRun run, DateTimeOffset now, CancellationToken ct)
    {
        var latest = await store.GetLatestGeneratorStatsAsync(run.Id, ct);
        var newest = run.RampStartedAt ?? run.StartedAt ?? run.QueuedAt;
        foreach (var line in latest)
        {
            if (line.At > newest)
            {
                newest = line.At;
            }
        }

        return now - newest > _options.StatsStaleAfter;
    }

    private async Task FleetUpAsync(IServiceProvider services, ILoadStore store, LoadRun run, DateTimeOffset now, CancellationToken ct)
    {
        var requested = run.FleetSizeRequested ?? 0;
        var group = await _fleetGroup.GetAsync(ct);
        if (group is null || requested < 1)
        {
            await DrainAsync(store, run, LoadStopReason.Failed, "fleet group is not configured", now, ct);
            return;
        }

        var size = await _scaler.DescribeGroupAsync(group, ct)
            ?? throw new InvalidOperationException($"fleet group {group} was not found");

        if (run.FleetSizeBefore is null)
        {
            // Recorded before the fleet moves, so a retried step never takes the raised
            // size for the original.
            var next = run.Clone();
            next.FleetSizeBefore = size.Desired;
            var recorded = await store.TryAdvanceRunAsync(run, next, ct);
            if (recorded is null)
            {
                return;
            }

            run = recorded;
        }

        if (size.Min != requested || size.Desired != requested || size.Max != requested)
        {
            // Min, max and desired together: the group's scaling policy would take a
            // raised desired alone back within minutes.
            await _scaler.SetGroupSizeAsync(group, requested, requested, requested, ct);
            _logger.LogInformation("Load run {RunId}: fleet group {Group} set to {Size}.", run.Id, group, requested);
            return;
        }

        var cutoff = now - _reconciler.InstanceStaleThreshold;
        var fresh = (await services.GetRequiredService<IInstanceStatusStore>().GetAllAsync(ct))
            .Count(i => i.HeartbeatAt >= cutoff);
        if (fresh == requested)
        {
            await AdvanceAsync(store, run, r => r.Status = LoadRunStatus.GeneratorsUp, now, null, ct);
        }
    }

    private async Task GeneratorsUpAsync(ILoadStore store, LoadRun run, DateTimeOffset now, CancellationToken ct)
    {
        if (run.SpecToken is not null)
        {
            var written = await WriteSpecSecretAsync(store, run, now, ct);
            if (written is null)
            {
                return;
            }

            run = written;
        }

        if (run.CheckedIn >= run.Generators)
        {
            await AdvanceAsync(store, run, r =>
            {
                r.Status = LoadRunStatus.Ramping;
                r.RampStartedAt = now;
            }, now, null, ct);
            return;
        }

        if (now - EnteredAt(run) >= _options.CheckInTimeout)
        {
            _logger.LogWarning(
                "Load run {RunId}: {CheckedIn} of {Generators} generators checked in within {Timeout}.",
                run.Id, run.CheckedIn, run.Generators, _options.CheckInTimeout);
            await DrainAsync(store, run, LoadStopReason.Failed, CheckInTimeoutDetail, now, ct);
            return;
        }

        var group = _options.GeneratorGroup!;
        var size = await _scaler.DescribeGroupAsync(group, ct)
            ?? throw new InvalidOperationException($"generator group {group} was not found");
        if (size.Desired != run.Generators)
        {
            await _scaler.SetDesiredAsync(group, run.Generators, ct);
            _logger.LogInformation("Load run {RunId}: generator group {Group} set to {Generators}.", run.Id, group, run.Generators);
        }
    }

    /// <summary>
    /// Write the generator spec secret and clear <c>spec_token</c> in one conditional
    /// update, so the secret is written once per run. Returns the run after the update,
    /// or null when the run moved on (failed for a missing setting, or the update lost).
    /// The secret's value is never logged.
    /// </summary>
    private async Task<LoadRun?> WriteSpecSecretAsync(ILoadStore store, LoadRun run, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.GeneratorSpecSecret))
        {
            await DrainAsync(store, run, LoadStopReason.Failed, "Load:GeneratorSpecSecret is not set", now, ct);
            return null;
        }

        if (string.IsNullOrWhiteSpace(_options.GatewayBaseUrl))
        {
            await DrainAsync(store, run, LoadStopReason.Failed, "Load:GatewayBaseUrl is not set", now, ct);
            return null;
        }

        var scenario = await store.GetScenarioAsync(run.Scenario, ct);
        if (scenario is null)
        {
            await DrainAsync(store, run, LoadStopReason.Failed, $"scenario {run.Scenario} no longer exists", now, ct);
            return null;
        }

        var spec = SpecSecretJson(run.Id, _options.GatewayBaseUrl, run.SpecToken!, scenario.Image, scenario.Tag);
        await _secrets.PutSecretStringAsync(_options.GeneratorSpecSecret, spec, ct);

        var next = run.Clone();
        next.SpecToken = null;
        var after = await store.TryAdvanceRunAsync(run, next, ct);
        if (after is not null)
        {
            _logger.LogInformation(
                "Load run {RunId}: wrote the generator spec secret {Secret}.", run.Id, _options.GeneratorSpecSecret);
        }

        return after;
    }

    /// <summary>
    /// The generator spec secret (docs/load.md section 5):
    /// <c>{ runId, gatewayBaseUrl, token, image, tag }</c>.
    /// </summary>
    public static string SpecSecretJson(long runId, string gatewayBaseUrl, string token, string image, string tag) =>
        new JsonObject
        {
            ["runId"] = runId,
            ["gatewayBaseUrl"] = gatewayBaseUrl,
            ["token"] = token,
            ["image"] = image,
            ["tag"] = tag,
        }.ToJsonString();

    private async Task DrainingAsync(ILoadStore store, LoadRun run, DateTimeOffset now, CancellationToken ct)
    {
        var group = _options.GeneratorGroup!;
        var size = await _scaler.DescribeGroupAsync(group, ct)
            ?? throw new InvalidOperationException($"generator group {group} was not found");
        if (size.Desired > 0)
        {
            await _scaler.SetDesiredAsync(group, 0, ct);
        }

        if (run.RestoreFleet && run.FleetSizeBefore is { } before)
        {
            var fleet = await _fleetGroup.GetAsync(ct);
            if (fleet is not null)
            {
                var fleetSize = await _scaler.DescribeGroupAsync(fleet, ct);
                if (fleetSize is not null
                    && (fleetSize.Min != before || fleetSize.Desired != before || fleetSize.Max != before))
                {
                    await _scaler.SetGroupSizeAsync(fleet, before, before, before, ct);
                    _logger.LogInformation("Load run {RunId}: fleet group {Group} restored to {Size}.", run.Id, fleet, before);
                }
            }
        }

        if (size.InService == 0)
        {
            await FinishAsync(store, run, TerminalStatus(run.StopReason), null, now, ct);
        }
    }

    /// <summary>The terminal status a drained run ends in, by its <c>stop_reason</c>.</summary>
    public static string TerminalStatus(string? stopReason) => stopReason switch
    {
        null => LoadRunStatus.Succeeded,
        LoadStopReason.Operator => LoadRunStatus.Stopped,
        LoadStopReason.HardClock => LoadRunStatus.Expired,
        LoadStopReason.Lost or LoadStopReason.LeaderRestart => LoadRunStatus.Lost,
        _ => LoadRunStatus.Failed,
    };

    // ---------- transitions ----------

    /// <summary>
    /// Into <c>draining</c>: the generator group's desired capacity to 0 first (a throw
    /// leaves the run where it is for the next tick), then the conditional update with
    /// <paramref name="reason"/> (null for a normal finish) and <paramref name="detail"/>.
    /// </summary>
    private async Task<LoadRun?> DrainAsync(
        ILoadStore store, LoadRun run, string? reason, string? detail, DateTimeOffset now, CancellationToken ct)
    {
        await _scaler.SetDesiredAsync(_options.GeneratorGroup!, 0, ct);
        return await ToDrainingAsync(store, run, reason, detail, now, ct);
    }

    private Task<LoadRun?> ToDrainingAsync(
        ILoadStore store, LoadRun run, string? reason, string? detail, DateTimeOffset now, CancellationToken ct) =>
        AdvanceAsync(store, run, r =>
        {
            r.Status = LoadRunStatus.Draining;
            r.StopReason = reason;
            r.Detail = Truncate(detail) ?? r.Detail;
            r.SpecToken = null;
        }, now, detail ?? reason ?? "finished", ct);

    private async Task FinishAsync(
        ILoadStore store, LoadRun run, string status, string? detail, DateTimeOffset now, CancellationToken ct)
    {
        var after = await AdvanceAsync(store, run, r =>
        {
            r.Status = status;
            r.FinishedAt = now;
            r.Detail = Truncate(detail) ?? r.Detail;
            r.SpecToken = null;
        }, now, detail, ct);
        if (after is null)
        {
            return;
        }

        _driven.TryRemove(run.Id, out _);
        _failures.TryRemove(run.Id, out _);
        _sampledAt.TryRemove(run.Id, out _);
        _metrics.RunFinished(run.Scenario, status, after.StartedAt is { } started ? now - started : null);
        _logger.LogInformation("Load run {RunId} of scenario {Scenario} finished {Status}.", run.Id, run.Scenario, status);

        if (status == LoadRunStatus.Succeeded && run.Validation
            && await store.SetScenarioValidationAsync(run.Scenario, run.ScenarioVersion, run.Id, ct))
        {
            _logger.LogInformation("Load run {RunId} validated scenario {Scenario} version {Version}.",
                run.Id, run.Scenario, run.ScenarioVersion);
        }
    }

    /// <summary>
    /// One status change: <paramref name="change"/> applied to a copy, a timeline entry
    /// for the new status, the conditional update, then the <c>run</c> event. Null when
    /// the update matched nothing (a stop request or another leader got there first).
    /// </summary>
    private async Task<LoadRun?> AdvanceAsync(
        ILoadStore store, LoadRun run, Action<LoadRun> change, DateTimeOffset now, string? note, CancellationToken ct)
    {
        var next = run.Clone();
        change(next);
        next.Timeline = LoadJson.AppendTimeline(run.Timeline, now, next.Status, Actor, note);

        var after = await store.TryAdvanceRunAsync(run, next, ct);
        if (after is null)
        {
            _logger.LogInformation("Load run {RunId} changed before it could move to {Status}; reading it again next tick.",
                run.Id, next.Status);
            return null;
        }

        _logger.LogInformation("Load run {RunId}: {From} to {To}.", run.Id, run.Status, after.Status);
        Broadcast(after);
        return after;
    }

    /// <summary>
    /// The retry limit was reached: the generator group to 0 regardless (a throw is
    /// logged and the run moves on anyway; the orphan guard keeps trying once it is
    /// terminal), then <c>draining</c> with reason <c>failed</c>, or straight to
    /// <c>failed</c> when it was already draining.
    /// </summary>
    private async Task FailAfterRetriesAsync(long runId, Exception error)
    {
        var now = _time.GetUtcNow();
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ILoadStore>();
        var run = await store.GetRunAsync(runId, CancellationToken.None);
        if (run is null || LoadRunStatus.IsTerminal(run.Status))
        {
            return;
        }

        try
        {
            await _scaler.SetDesiredAsync(_options.GeneratorGroup!, 0, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Load run {RunId}: could not set the generator group to 0 while failing the run.", runId);
        }

        var detail = string.Create(CultureInfo.InvariantCulture,
            $"step failed {_options.ScaleRetryLimit} ticks in a row in {run.Status}: {error.Message}");
        _logger.LogError("Load run {RunId} failed: {Detail}", runId, detail);

        if (run.Status == LoadRunStatus.Draining)
        {
            await FinishAsync(store, run, LoadRunStatus.Failed, detail, now, CancellationToken.None);
        }
        else
        {
            await ToDrainingAsync(store, run, LoadStopReason.Failed, detail, now, CancellationToken.None);
        }
    }

    /// <summary>When the run entered its current status: the first of the trailing timeline entries with that status.</summary>
    private static DateTimeOffset EnteredAt(LoadRun run)
    {
        DateTimeOffset? entered = null;
        foreach (var entry in LoadJson.ParseTimeline(run.Timeline))
        {
            if (entry.Status != run.Status)
            {
                entered = null;
            }
            else
            {
                entered ??= entry.At;
            }
        }

        return entered ?? run.StartedAt ?? run.QueuedAt;
    }

    private static string? Truncate(string? detail) => Jobs.JobSchedulerService.TruncateDetail(detail);

    private void Broadcast(LoadRun run)
    {
        if (_publisher is not null)
        {
            LoadEndpoints.BroadcastRun(_publisher, run);
        }
    }

    // ---------- fleet samples ----------

    /// <summary>
    /// Every <see cref="LoadOptions.FleetSampleInterval"/> while the run is not terminal:
    /// one <c>fleet</c> stats row from the fresh <c>instance_status</c> rows and the
    /// presence count of the run's <c>params.channel</c>, and a <c>fleet</c> event.
    /// </summary>
    private async Task SampleFleetAsync(IServiceProvider services, ILoadStore store, long runId, DateTimeOffset now, CancellationToken ct)
    {
        if (_sampledAt.TryGetValue(runId, out var last) && now - last < _options.FleetSampleInterval)
        {
            return;
        }

        var run = await store.GetRunAsync(runId, ct);
        if (run is null || LoadRunStatus.IsTerminal(run.Status))
        {
            return;
        }

        var cutoff = now - _reconciler.InstanceStaleThreshold;
        var instances = new JsonArray();
        foreach (var row in (await services.GetRequiredService<IInstanceStatusStore>().GetAllAsync(ct))
            .Where(r => r.HeartbeatAt >= cutoff)
            .OrderBy(r => r.InstanceId, StringComparer.Ordinal))
        {
            instances.Add(InstanceSample(row));
        }

        var sample = new JsonObject
        {
            ["at"] = now,
            ["instances"] = instances,
            ["channelMembers"] = await ChannelMembersAsync(run, ct),
        };

        await store.AppendStatAsync(new LoadRunStat
        {
            RunId = runId,
            Source = LoadStatSource.Fleet,
            Index = null,
            At = now,
            Body = sample.ToJsonString(Web),
        }, ct);
        _sampledAt[runId] = now;

        if (_publisher is not null)
        {
            LoadEndpoints.BroadcastFleet(_publisher, runId, now, sample);
        }
    }

    /// <summary>
    /// One instance of a fleet sample, from what its heartbeat row carries: id, leader
    /// flag, heartbeat time, gateway version and the count of running containers. The
    /// heartbeat row has no cpu, memory, disk or hub connection figures, so those fields
    /// are null.
    /// </summary>
    private static JsonObject InstanceSample(InstanceStatus row) => new()
    {
        ["instanceId"] = row.InstanceId,
        ["leader"] = row.IsLeader,
        ["heartbeatAt"] = row.HeartbeatAt,
        ["gatewayVersion"] = row.GatewayVer,
        ["runningServices"] = InstanceServicesJson.Parse(row.Services).Count(s => s.State == "running"),
        ["cpu"] = null,
        ["memory"] = null,
        ["disk"] = null,
        ["hubConnections"] = null,
    };

    private async Task<int?> ChannelMembersAsync(LoadRun run, CancellationToken ct)
    {
        if (_presence is null
            || LoadJson.Element(run.Params) is not { ValueKind: JsonValueKind.Object } parameters
            || !parameters.TryGetProperty("channel", out var channel)
            || channel.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(channel.GetString()))
        {
            return null;
        }

        try
        {
            return await _presence.CountAsync(channel.GetString()!, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Load run {RunId}: presence count of the run's channel failed.", run.Id);
            return null;
        }
    }

    // The orphan guard's audit write wants a publisher; a host without one gets this.
    private sealed class NoPublisher : IChannelEventPublisher
    {
        public static readonly NoPublisher Instance = new();

        public Task PublishAsync(string channel, string @event, object data, CancellationToken ct = default) => Task.CompletedTask;

        public void TryPublish(string channel, string @event, object data)
        {
        }
    }
}
