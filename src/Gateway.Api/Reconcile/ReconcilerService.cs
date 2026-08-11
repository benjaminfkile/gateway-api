using Gateway.Api.Containers;
using Gateway.Api.Data;
using Gateway.Api.Instances;
using Gateway.Api.Management;
using Gateway.Api.Manifest;
using Gateway.Api.Proxy;
using Gateway.Api.RealTime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Reconcile;

/// <summary>
/// The node reconciler (tech-spec §4.3, §7). Every ~30s (± jitter) it compares
/// the desired-state manifest against the containers actually running on this box
/// and converges the difference: starting, stopping, or blue-green-replacing
/// containers. It only ever mutates its <b>own</b> box.
/// <para>
/// Disabled by default: the loop does nothing unless
/// <c>GATEWAY_RECONCILER_ENABLED=true</c> (<see cref="ReconcilerOptions.Enabled"/>),
/// so the build/test environment — which has no Docker daemon — is never touched.
/// The convergence logic is a pure function (<see cref="ReconcilePlanner"/>) and
/// the Docker interaction is behind <see cref="IContainerRuntime"/>, so the whole
/// flow is unit-tested against a fake runtime.
/// </para>
/// </summary>
public sealed class ReconcilerService : BackgroundService
{
    private readonly IContainerRuntime _runtime;
    private readonly ProxyStateService _proxyState;
    private readonly IServiceAddressResolver _addressResolver;
    private readonly ServiceHostPortMap _hostPorts;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadinessProber _readinessProber;
    private readonly IReconcileReporter _reporter;
    private readonly InstanceMetadataProvider _metadata;
    private readonly ILeaderElection _leaderElection;
    private readonly ReconcilerOptions _options;
    private readonly ILogger<ReconcilerService> _logger;
    private readonly MigrationReadinessGate? _migrationGate;

    // Real-time broadcaster for terminal deploy + per-instance convergence events
    // (tech-spec §4.2, §4.5). Optional: a DB-less / minimal test host may run the
    // reconciler with no hub wired, so this stays null and every publish is skipped.
    // Broadcasts always go through TryPublish so a backplane blip can never affect
    // reconciliation; on the leader the Redis backplane fans them out fleet-wide.
    private readonly IChannelEventPublisher? _publisher;

    // Service log groups whose 30-day retention has already been ensured this
    // process, so PutRetentionPolicy is issued at most once per group (tech-spec §9).
    private readonly HashSet<string> _retentionEnsured = new(StringComparer.Ordinal);

    // Most recent failed reconcile outcome per service, cleared on a subsequent
    // success. Merged into the heartbeat's services JSON so convergence failures are
    // visible through the management API instead of only journald (tech-spec §4.4,
    // production 2026-08-08). Guarded because the heartbeat reads it while actions write it.
    private readonly Dictionary<string, ServiceError> _serviceErrors = new(StringComparer.Ordinal);

    // Services whose most recent error came from environment resolution (tech-spec §8),
    // so a subsequent successful resolution clears it. Guarded by the _serviceErrors lock.
    private readonly HashSet<string> _envErrored = new(StringComparer.Ordinal);

    // Fleet-event state, touched only from the single reconcile loop (no lock needed).
    // Was this instance the leader on the previous loop, so a "leaderChange" fires only
    // on the transition into leadership (task #588). Starts false: a fresh process that
    // wins the election on its first loop announces itself.
    private bool _wasLeader;

    // Live instance ids this leader saw on its previous loop, so an "instances" event
    // reports only newly-joined ids (task #588). Null until the first leader observation,
    // which seeds silently so acquiring leadership does not report the whole fleet as
    // joined. Only read/written on the leader path of the single reconcile loop.
    private HashSet<string>? _knownInstanceIds;

    public ReconcilerService(
        IContainerRuntime runtime,
        ProxyStateService proxyState,
        IServiceAddressResolver addressResolver,
        ServiceHostPortMap hostPorts,
        IServiceScopeFactory scopeFactory,
        IReadinessProber readinessProber,
        IReconcileReporter reporter,
        InstanceMetadataProvider metadata,
        ILeaderElection leaderElection,
        ReconcilerOptions options,
        ILogger<ReconcilerService> logger,
        MigrationReadinessGate? migrationGate = null,
        IChannelEventPublisher? publisher = null)
    {
        _runtime = runtime;
        _proxyState = proxyState;
        _addressResolver = addressResolver;
        _hostPorts = hostPorts;
        _scopeFactory = scopeFactory;
        _readinessProber = readinessProber;
        _reporter = reporter;
        _metadata = metadata;
        _leaderElection = leaderElection;
        _options = options;
        _logger = logger;
        _migrationGate = migrationGate;
        _publisher = publisher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation(
                "Node reconciler disabled ({EnvVar} != true); not managing any containers.",
                ReconcilerOptions.EnabledEnvVar);
            return;
        }

        // Gate on schema migration (tech-spec §6): with a database configured, do
        // not converge or heartbeat until the migration hosted service has applied
        // pending migrations — otherwise every manifest/status query races a schema
        // that may not exist yet. The gate is already open when no DB is configured.
        if (_migrationGate is not null)
        {
            _logger.LogInformation("Waiting for database migrations to complete before reconciling.");
            try
            {
                await _migrationGate.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }

        _logger.LogInformation("Node reconciler enabled; converging every {Interval} (± {Jitter}).",
            _options.Interval, _options.MaxJitter);

        try
        {
            await _runtime.EnsureNetworkAsync(_options.Network, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to ensure Docker network {Network}", _options.Network);
        }

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
                // A reconcile failure must never crash the loop; log and try again next tick.
                _logger.LogError(ex, "Reconcile loop iteration failed");
            }

            try
            {
                await Task.Delay(NextDelay(), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Run a single reconcile pass: read desired state and actual containers,
    /// compute a plan, and execute it. Public so the flow can be driven directly
    /// in tests without the timed loop.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        var (desired, envFailed) = await BuildDesiredAsync(ct);
        var actual = await _runtime.ListManagedContainersAsync(ct);

        // Reconcile the container-truth host-port map + routes before acting, from the
        // ACTUAL running containers' published ports (tech-spec §7, production 2026-08-08).
        // A port that changed outside a reconciler action — Docker restart-policy
        // recovery, manual intervention, or a crash-looper that finally stabilized on a
        // (possibly new-to-us) port — updates the map and refreshes YARP within one loop,
        // with no gateway restart. Services mid blue-green keep their destination override
        // (skipped so it is not clobbered), and routes rebuild only on a real change so a
        // steady fleet churns nothing.
        var midSwap = _proxyState.ServicesWithDestinationOverride();
        if (_hostPorts.ReconcileFrom(actual, midSwap))
        {
            await _proxyState.RefreshRoutesAsync(ct);
        }

        var plan = ReconcilePlanner.Plan(desired, actual);

        // Forget errors for services that are no longer desired and have no container
        // (e.g. a deleted manifest row whose start had been failing) so a removed
        // service does not linger as a phantom absent-error entry in the heartbeat.
        // Services whose env failed to resolve this loop are kept so their error still
        // surfaces even though they were dropped from the desired set (tech-spec §8).
        PruneErrors(desired, actual, envFailed);

        foreach (var action in plan)
        {
            ct.ThrowIfCancellationRequested();
            await ExecuteAsync(action, ct);
        }

        // Derive leadership from heartbeats (tech-spec §4.3): the election upserts our
        // own heartbeat first — so a booting instance sees itself — then picks the
        // lowest live instance_id. We do this right before publishing our heartbeat so
        // the fresh full row below carries the authoritative is_leader for the loop.
        var isLeader = await TryAcquireLeadershipAsync(ct);

        // Publish this instance's heartbeat + inventory so any instance can answer
        // fleet-wide queries (tech-spec §4.3, §4.4).
        await HeartbeatAsync(isLeader, ct);

        // Close the deploy loop (tech-spec §4.5, §7): report this instance's
        // per-deploy convergence and — as leader — mark a deploy done/partial once
        // every live instance has converged.
        await ReconcileDeployProgressAsync(isLeader, ct);
    }

    private async Task<bool> TryAcquireLeadershipAsync(CancellationToken ct)
    {
        try
        {
            return await _leaderElection.TryAcquireAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never let a leadership hiccup stop convergence; just run as a follower.
            _logger.LogWarning(ex, "Leader election failed; running as non-leader this loop.");
            return false;
        }
    }

    /// <summary>
    /// Upsert this instance's <c>instance_status</c> row (ips, gateway version,
    /// leader flag, per-service inventory, heartbeat=now). When this instance is the
    /// leader, also prune rows whose heartbeat has gone stale — the only fleet-wide
    /// duty in this task (tech-spec §4.3, §4.4).
    /// </summary>
    private async Task HeartbeatAsync(bool isLeader, CancellationToken ct)
    {
        try
        {
            var identity = await _metadata.GetAsync(ct);
            var containers = await _runtime.ListManagedContainersAsync(ct);

            var status = new InstanceStatus
            {
                InstanceId = identity.InstanceId,
                PrivateIp = identity.PrivateIp,
                PublicIp = identity.PublicIp,
                GatewayVer = GatewayVersion.Current,
                IsLeader = isLeader,
                Services = InstanceServicesJson.Build(containers, SnapshotErrors()),
                HeartbeatAt = DateTimeOffset.UtcNow,
            };

            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IInstanceStatusStore>();

            await store.UpsertAsync(status, ct);

            if (isLeader)
            {
                await PublishFleetEventsAsync(store, identity.InstanceId, ct);
            }

            // Remember this loop's leadership ONLY after the fleet events published, as the
            // LAST statement in the try (task #608 finding 4). If the heartbeat body threw
            // before this — before PublishFleetEventsAsync ran — _wasLeader is left
            // unchanged so the leaderChange edge is NOT consumed and the next loop retries
            // it, rather than permanently swallowing the announcement for this term.
            _wasLeader = isLeader;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A heartbeat failure must not crash the loop; the next tick retries.
            _logger.LogError(ex, "Failed to publish instance_status heartbeat");
        }
    }

    /// <summary>
    /// Broadcast the leader-only fleet events on <c>ops:fleet</c> (tech-spec §4.3, §4.4;
    /// task #588): prune stale <c>instance_status</c> rows, then publish a per-cycle
    /// <c>heartbeat</c> liveness signal, a <c>leaderChange</c> on the transition into
    /// leadership, and an <c>instances</c> event whenever rows were pruned or new
    /// instances appeared. Only the leader publishes so a fleet of N emits each event
    /// once; the Redis backplane fans them out to every connected client. Every send is
    /// <see cref="IChannelEventPublisher.TryPublish"/> so a backplane blip can never
    /// disturb the prune that already committed.
    /// </summary>
    private async Task PublishFleetEventsAsync(IInstanceStatusStore store, string leaderInstanceId, CancellationToken ct)
    {
        var becameLeader = !_wasLeader;

        // Snapshot the fleet before pruning: DeleteStaleAsync returns only a count, so we
        // derive the exact pruned ids (and the surviving live set) from this read. Our own
        // row was just upserted with heartbeat=now, so it is always in the live set.
        var rows = await store.GetAllAsync(ct);
        var cutoff = DateTimeOffset.UtcNow - _options.InstanceStaleThreshold;
        var prunedIds = rows.Where(r => r.HeartbeatAt < cutoff).Select(r => r.InstanceId).ToList();

        var pruned = await store.DeleteStaleAsync(cutoff, ct);
        if (pruned > 0)
        {
            _logger.LogInformation(
                "Leader pruned {Count} stale instance_status row(s) older than {Threshold}.",
                pruned, _options.InstanceStaleThreshold);
        }

        var liveIds = rows
            .Where(r => r.HeartbeatAt >= cutoff)
            .Select(r => r.InstanceId)
            .ToHashSet(StringComparer.Ordinal);

        // Newly-joined relative to what this leader last saw. On the first leader
        // observation (null) seed silently so acquiring leadership does not announce the
        // whole fleet as joined.
        var joinedIds = _knownInstanceIds is null
            ? new List<string>()
            : liveIds.Where(id => !_knownInstanceIds.Contains(id)).ToList();
        _knownInstanceIds = liveIds;

        if (_publisher is null)
        {
            return;
        }

        // leaderChange first: announce that THIS instance is now the leader.
        if (becameLeader)
        {
            _publisher.TryPublish(ManagementEndpoints.OpsFleetChannel, "leaderChange", new
            {
                instanceId = leaderInstanceId,
            });
        }

        // The end-to-end liveness heartbeat (NOT a transport keepalive): proves the
        // hub + backplane + group delivery path works. Emitted every reconcile cycle.
        _publisher.TryPublish(ManagementEndpoints.OpsFleetChannel, "heartbeat", new
        {
            ts = DateTimeOffset.UtcNow.ToString("O"),
            leaderInstanceId,
            instanceCount = liveIds.Count,
        });

        // Membership churn: omit entirely when nothing joined and nothing was pruned.
        if (joinedIds.Count > 0 || prunedIds.Count > 0)
        {
            _publisher.TryPublish(ManagementEndpoints.OpsFleetChannel, "instances", new
            {
                joined = joinedIds,
                pruned = prunedIds,
            });
        }
    }

    /// <summary>
    /// Drive in-progress deploys toward completion (tech-spec §4.5, §7). Every
    /// instance upserts its own <c>deploy_instance_status</c> row once it is running
    /// the deploy's target digest; the leader then marks the <c>deploy_history</c>
    /// row <c>done</c> when every live instance has converged, or <c>partial</c> when
    /// a live instance has reported a failure. Optional: when no <see cref="IDeployStore"/>
    /// is registered (DB-less box / minimal test host) this is a no-op.
    /// </summary>
    private async Task ReconcileDeployProgressAsync(bool isLeader, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var deployStore = scope.ServiceProvider.GetService<IDeployStore>();
        if (deployStore is null)
        {
            return;
        }

        try
        {
            var inProgress = await deployStore.ListInProgressAsync(ct);
            if (inProgress.Count == 0)
            {
                return;
            }

            var identity = await _metadata.GetAsync(ct);
            var containers = await _runtime.ListManagedContainersAsync(ct);

            // This instance's running digest per canonical service name.
            var running = containers
                .Where(c => string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase))
                .GroupBy(c => c.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Digest, StringComparer.Ordinal);

            foreach (var deploy in inProgress)
            {
                if (running.TryGetValue(deploy.Service, out var digest)
                    && string.Equals(digest, deploy.ToDigest, StringComparison.Ordinal))
                {
                    await deployStore.UpsertInstanceStatusAsync(new DeployInstanceStatus
                    {
                        DeployId = deploy.Id,
                        InstanceId = identity.InstanceId,
                        Status = DeployInstanceState.Converged,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    }, ct);

                    // Broadcast this instance's convergence (tech-spec §4.5) so the dashboard
                    // can tick the rollout count up live. Best-effort: a publish failure must
                    // never affect the reconcile that already committed the status row.
                    _publisher?.TryPublish(ManagementEndpoints.OpsDeploysChannel, "deployInstance", new
                    {
                        deployId = deploy.Id,
                        instanceId = identity.InstanceId,
                        status = DeployInstanceState.Converged,
                        error = (string?)null,
                    });
                }
            }

            if (!isLeader)
            {
                return;
            }

            // Leader: complete deploys once the whole live fleet has converged.
            var statusStore = scope.ServiceProvider.GetRequiredService<IInstanceStatusStore>();
            var cutoff = DateTimeOffset.UtcNow - _options.InstanceStaleThreshold;
            var live = (await statusStore.GetAllAsync(ct))
                .Where(i => i.HeartbeatAt >= cutoff)
                .ToList();

            if (live.Count == 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var deploy in inProgress)
            {
                var converged = live.Count(i => InstanceRunsDigest(i, deploy.Service, deploy.ToDigest));
                if (converged == live.Count)
                {
                    await MarkDeployAsync(deployStore, deploy, DeployStatus.Done, ct);
                    continue;
                }

                // A live instance reported an explicit convergence failure (bug #1), or the
                // deploy has been running past the staleness timeout (bug: an in_progress
                // deploy would otherwise be rescanned forever): terminate it. It is
                // "partial" when some instances converged and "failed" when none did.
                var childStatuses = await deployStore.ListInstanceStatusesAsync(deploy.Id, ct);
                var failure = childStatuses.FirstOrDefault(
                    s => string.Equals(s.Status, DeployInstanceState.Failed, StringComparison.Ordinal));
                var timedOut = now - deploy.StartedAt > _options.DeployTimeout;

                if (failure is null && !timedOut)
                {
                    // Still rolling out and within the timeout — leave it in progress.
                    continue;
                }

                var status = converged > 0 ? DeployStatus.Partial : DeployStatus.Failed;
                var error = failure?.Detail
                    ?? (timedOut ? "deploy timed out" : "reconcile failed on one or more instances");
                await MarkDeployAsync(deployStore, deploy, status, ct, error);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deploy-progress bookkeeping must never crash the loop; retry next tick.
            _logger.LogError(ex, "Failed to reconcile deploy progress");
        }
    }

    private static bool InstanceRunsDigest(InstanceStatus instance, string service, string? digest)
    {
        return InstanceServicesJson.Parse(instance.Services).Any(e =>
            string.Equals(e.Name, service, StringComparison.Ordinal)
            && string.Equals(e.State, "running", StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.Digest, digest, StringComparison.Ordinal));
    }

    /// <summary>
    /// Transition a deploy to a terminal <paramref name="status"/> (done/partial/failed):
    /// stamp <c>FinishedAt</c>, persist, then broadcast the terminal <c>deploy</c> event on
    /// <c>ops:deploys</c> using the ChannelEvent envelope (tech-spec §4.2, §4.5). The
    /// broadcast rides <see cref="IChannelEventPublisher.TryPublish"/> so a backplane
    /// failure can never affect the transition that has already committed; on the leader
    /// the Redis backplane fans it out to clients on every instance. <paramref name="error"/>
    /// is null unless the deploy failed.
    /// </summary>
    private async Task MarkDeployAsync(
        IDeployStore store, DeployHistory deploy, string status, CancellationToken ct, string? error = null)
    {
        deploy.Status = status;
        deploy.FinishedAt = DateTimeOffset.UtcNow;
        // Persist the terminal failure reason, not just ride it on the fire-and-forget
        // event (task #608 finding 5): events are hints, the store is truth, so GET
        // /mgmt/deploys must return a non-null detail for a timed-out / failed deploy.
        deploy.Detail = error;
        await store.UpdateAsync(deploy, ct);

        _publisher?.TryPublish(ManagementEndpoints.OpsDeploysChannel, "deploy", new
        {
            deployId = deploy.Id,
            service = deploy.Service,
            action = deploy.Action,
            fromDigest = deploy.FromDigest,
            toDigest = deploy.ToDigest,
            status = deploy.Status,
            // Terminal now, so FinishedAt is set; emit it as an ISO 8601 string per the
            // wire contract (round-trip "O" format).
            finishedAt = deploy.FinishedAt?.ToString("O"),
            error,
        });
    }

    /// <summary>
    /// Project the manifest into desired services, resolving each one's environment
    /// (tech-spec §8). Env resolution is isolated per service: a missing secret /
    /// AccessDenied / parse failure fails only <b>that</b> service — it is dropped from
    /// this pass (its existing container, if any, is left untouched and keeps serving)
    /// and its ref-only error is recorded through the same last-error plumbing as any
    /// other reconcile failure, never taking down the loop or other services. Returns
    /// the resolved services plus the names whose env failed (so their error is not
    /// pruned as a phantom while they are absent from the desired set).
    /// </summary>
    private async Task<(IReadOnlyList<DesiredService> Desired, IReadOnlyList<string> EnvFailed)> BuildDesiredAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IManifestStore>();
        var envProvider = scope.ServiceProvider.GetRequiredService<IServiceEnvProvider>();

        var manifests = await store.GetAllAsync(ct);
        var desired = new List<DesiredService>(manifests.Count);
        var envFailed = new List<string>();
        foreach (var m in manifests)
        {
            IReadOnlyDictionary<string, string> env;
            try
            {
                env = await envProvider.GetEnvAsync(m, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Failure isolation (requirement #4): surface a ref-only error for this
                // service and skip it so its container is left as-is. Values never reach
                // here — the provider only throws messages naming the ref/name.
                _logger.LogError(ex, "Failed to resolve environment for {Service}", m.Name);
                envFailed.Add(m.Name);
                await RecordEnvErrorAsync(m.Name, ex.Message, ct);
                continue;
            }

            // Env resolved: drop any prior env-resolution error for this service and, on a
            // real clear transition, broadcast the cleared serviceError (task #588). This
            // path removes the error outside ReportOutcomeAsync, so it publishes here.
            if (ClearEnvErrorIfPresent(m.Name))
            {
                await PublishServiceErrorAsync(m.Name, null, ct);
            }

            // Inject the per-service real-time publish token so the container can
            // authorize POST /internal/publish for its own channels (task #593). Folded
            // into the resolved env before hashing so a first-time token mint (or a
            // rotation) drifts the env hash and blue-green-recreates the container with
            // the token present. Pre-migration rows have no token yet; they simply run
            // without one until the next upsert generates it.
            env = WithRealtimeToken(env, m.RealtimePublishToken);

            desired.Add(new DesiredService(
                Name: m.Name,
                Image: m.Image,
                Tag: m.Tag,
                Digest: m.Digest,
                Port: m.Port,
                DesiredStatus: m.DesiredStatus,
                EnvHash: EnvHasher.Compute(env),
                EnvVars: env,
                RestartRequestedAt: m.RestartRequestedAt));
        }

        return (desired, envFailed);
    }

    /// <summary>
    /// Return the resolved env with <c>GATEWAY_REALTIME_TOKEN</c> added when the
    /// service has a publish token (task #593). Copied into a fresh map so the
    /// provider's (possibly shared/immutable) dictionary is never mutated; a service
    /// with no token yet is returned unchanged.
    /// </summary>
    private static IReadOnlyDictionary<string, string> WithRealtimeToken(
        IReadOnlyDictionary<string, string> env, string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return env;
        }

        var merged = new Dictionary<string, string>(env, StringComparer.Ordinal)
        {
            [RealtimePublishToken.ContainerEnvVar] = token,
        };
        return merged;
    }

    /// <summary>
    /// Record an environment-resolution failure for a service (tech-spec §8): tracks it
    /// as an env error so a later successful resolution clears it, and routes it through
    /// the shared reconcile-outcome plumbing so it lands in the service's heartbeat
    /// <c>lastError</c>. The detail is a ref-only message; secret values never appear.
    /// </summary>
    private Task RecordEnvErrorAsync(string service, string detail, CancellationToken ct)
    {
        lock (_serviceErrors)
        {
            _envErrored.Add(service);
        }

        // Route through the same reporter path as any failed action; there is no action
        // kind for env resolution, so report it as None (no container change).
        return ReportOutcomeAsync(new ReconcileOutcome(
            service, ReconcileActionKind.None, ReconcileOutcomeStatus.Failed, detail), ct);
    }

    /// <summary>
    /// Clear a service's recorded env-resolution error once its env resolves again.
    /// Returns true when an error was actually cleared (a transition worth broadcasting).
    /// </summary>
    private bool ClearEnvErrorIfPresent(string service)
    {
        lock (_serviceErrors)
        {
            if (_envErrored.Remove(service))
            {
                return _serviceErrors.Remove(service);
            }
        }

        return false;
    }

    /// <summary>
    /// Broadcast a service's last-error transition on <c>ops:fleet</c> (task #588):
    /// <c>{ service, instanceId, lastError, lastErrorAt }</c> with <paramref name="error"/>
    /// null on a clear. Per-instance — the payload carries this instance's id so the
    /// dashboard can attribute the error. Best-effort via
    /// <see cref="IChannelEventPublisher.TryPublish"/>; a missing publisher or an identity
    /// lookup failure is swallowed so the reconcile that already committed is untouched.
    /// </summary>
    private async Task PublishServiceErrorAsync(string service, ServiceError? error, CancellationToken ct)
    {
        if (_publisher is null)
        {
            return;
        }

        string instanceId;
        try
        {
            instanceId = (await _metadata.GetAsync(ct)).InstanceId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to resolve instance identity to broadcast serviceError for {Service}", service);
            return;
        }

        _publisher.TryPublish(ManagementEndpoints.OpsFleetChannel, "serviceError", new
        {
            service,
            instanceId,
            lastError = error?.Message,
            lastErrorAt = error?.At.ToString("O"),
        });
    }

    private Task ExecuteAsync(ReconcileAction action, CancellationToken ct) => action.Kind switch
    {
        ReconcileActionKind.Start => StartAsync(action, ct),
        ReconcileActionKind.StopRemove => StopRemoveAsync(action, ct),
        ReconcileActionKind.BlueGreenReplace => BlueGreenReplaceAsync(action, ct),
        _ => Task.CompletedTask,
    };

    /// <summary>
    /// Report a reconcile outcome and record it as this service's last-error state
    /// (tech-spec §4.4): a failure sets <see cref="ServiceError"/> for the service; a
    /// success clears it. The state is merged into the next heartbeat's services JSON,
    /// so a service failing every loop — even one with no container (absent) — is
    /// visible through the management API. Trimmed to keep the jsonb small.
    /// </summary>
    private async Task ReportOutcomeAsync(ReconcileOutcome outcome, CancellationToken ct)
    {
        ServiceError? current;
        bool changed;
        lock (_serviceErrors)
        {
            _serviceErrors.TryGetValue(outcome.ServiceName, out var previous);
            if (outcome.Status == ReconcileOutcomeStatus.Succeeded)
            {
                _serviceErrors.Remove(outcome.ServiceName);
                current = null;
                // Cleared transition only when there was an error to clear.
                changed = previous is not null;
            }
            else
            {
                current = new ServiceError(InstanceServicesJson.TrimError(outcome.Detail), DateTimeOffset.UtcNow);
                _serviceErrors[outcome.ServiceName] = current;
                // Set transition on first failure or when the message text changed;
                // a service failing identically every loop must not re-broadcast.
                changed = previous is null || !string.Equals(previous.Message, current.Message, StringComparison.Ordinal);
            }
        }

        // Broadcast the last-error transition on ops:fleet (task #588). Per-instance (the
        // payload carries this instance's id) and best-effort — a publish failure must
        // never affect the outcome that already committed.
        if (changed)
        {
            await PublishServiceErrorAsync(outcome.ServiceName, current, ct);
        }

        // A failed action that was servicing an active deploy must be correlated back to
        // the deploy row (bugs #1/#2): record this instance's failure so the leader's
        // progress scan can drive the deploy to a terminal partial/failed state instead
        // of leaving it in_progress forever. Only Start and BlueGreenReplace service a
        // deploy; a StopRemove or an env-resolution (None) outcome does not.
        if (outcome.Status == ReconcileOutcomeStatus.Failed
            && (outcome.Kind == ReconcileActionKind.Start || outcome.Kind == ReconcileActionKind.BlueGreenReplace))
        {
            await RecordDeployInstanceFailureAsync(outcome.ServiceName, outcome.Detail, ct);
        }

        await _reporter.ReportAsync(outcome, ct);
    }

    /// <summary>
    /// Correlate a failed reconcile action back to any <c>in_progress</c> deploy for the
    /// service (tech-spec §4.5, §7): write a <see cref="DeployInstanceState.Failed"/>
    /// <c>deploy_instance_status</c> row for this instance, keyed by the matching
    /// deploy's id, and broadcast a per-instance <c>deployInstance</c> failure event —
    /// the mirror of the convergence broadcast. Best-effort and DB-optional: no
    /// <see cref="IDeployStore"/> (DB-less box / minimal test host) or no matching
    /// in-progress deploy is a no-op, and any bookkeeping failure is logged rather than
    /// allowed to disturb the reconcile that already ran.
    /// </summary>
    private async Task RecordDeployInstanceFailureAsync(string service, string error, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var deployStore = scope.ServiceProvider.GetService<IDeployStore>();
            if (deployStore is null)
            {
                return;
            }

            var inProgress = await deployStore.ListInProgressAsync(ct);
            var matches = inProgress
                .Where(d => string.Equals(d.Service, service, StringComparison.Ordinal))
                .ToList();
            if (matches.Count == 0)
            {
                return;
            }

            var identity = await _metadata.GetAsync(ct);
            var detail = InstanceServicesJson.TrimError(error);

            foreach (var deploy in matches)
            {
                await deployStore.UpsertInstanceStatusAsync(new DeployInstanceStatus
                {
                    DeployId = deploy.Id,
                    InstanceId = identity.InstanceId,
                    Status = DeployInstanceState.Failed,
                    Detail = detail,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }, ct);

                // Mirror the convergence broadcast (tech-spec §4.5) so the dashboard can
                // show this instance failing to roll out live. Best-effort: a publish
                // failure must never affect the status row that already committed.
                _publisher?.TryPublish(ManagementEndpoints.OpsDeploysChannel, "deployInstance", new
                {
                    deployId = deploy.Id,
                    instanceId = identity.InstanceId,
                    status = DeployInstanceState.Failed,
                    error = detail,
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to record deploy-instance failure for {Service}", service);
        }
    }

    /// <summary>Snapshot the per-service error state for a heartbeat build (see <see cref="ReportOutcomeAsync"/>).</summary>
    private IReadOnlyDictionary<string, ServiceError> SnapshotErrors()
    {
        lock (_serviceErrors)
        {
            return new Dictionary<string, ServiceError>(_serviceErrors, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Drop tracked errors for services that are neither desired nor present as a
    /// container, so a service removed from the manifest stops surfacing an absent
    /// error entry. Errors for a desired service (still failing) or one that still has
    /// a container are kept until its next successful action clears them.
    /// </summary>
    private void PruneErrors(
        IReadOnlyList<DesiredService> desired,
        IReadOnlyList<ContainerInfo> actual,
        IReadOnlyList<string> envFailed)
    {
        lock (_serviceErrors)
        {
            if (_serviceErrors.Count == 0)
            {
                return;
            }

            var keep = new HashSet<string>(desired.Select(d => d.Name), StringComparer.Ordinal);
            keep.UnionWith(actual.Select(c => c.Name));
            // A service dropped from the desired set this loop because its env failed to
            // resolve still needs its error surfaced (tech-spec §8), so keep it.
            keep.UnionWith(envFailed);

            foreach (var name in _serviceErrors.Keys.Where(n => !keep.Contains(n)).ToList())
            {
                _serviceErrors.Remove(name);
                _envErrored.Remove(name);
            }
        }
    }

    private async Task StartAsync(ReconcileAction action, CancellationToken ct)
    {
        var d = action.Desired!;
        try
        {
            // Docker assigns the host port; record what it actually bound so the
            // proxy forwards there (the manifest port is container-internal only).
            var hostPort = await StartWithStaleDigestFallbackAsync(d, d.Name, ct);
            _hostPorts.Set(d.Name, hostPort);
            await _proxyState.RefreshRoutesAsync(ct);
            // The start may have created the CloudWatch group; set retention once (§9).
            await EnsureRetentionAsync(d.Name, ct);
            await ReportOutcomeAsync(new ReconcileOutcome(
                d.Name, action.Kind, ReconcileOutcomeStatus.Succeeded, action.Reason), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to start {Service}", d.Name);
            await ReportOutcomeAsync(new ReconcileOutcome(
                d.Name, action.Kind, ReconcileOutcomeStatus.Failed, ex.Message), ct);
        }
    }

    private async Task StopRemoveAsync(ReconcileAction action, CancellationToken ct)
    {
        try
        {
            await _runtime.StopAndRemoveAsync(action.ServiceName, ct);
            _hostPorts.Remove(action.ServiceName);
            await _proxyState.RefreshRoutesAsync(ct);
            await ReportOutcomeAsync(new ReconcileOutcome(
                action.ServiceName, action.Kind, ReconcileOutcomeStatus.Succeeded, action.Reason), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to stop/remove {Service}", action.ServiceName);
            await ReportOutcomeAsync(new ReconcileOutcome(
                action.ServiceName, action.Kind, ReconcileOutcomeStatus.Failed, ex.Message), ct);
        }
    }

    /// <summary>
    /// Zero-downtime replace (tech-spec §7): pull, start <c>{name}-green</c> on a
    /// Docker-assigned host port, poll readiness; on success swap the YARP
    /// destination to green, drain, then remove the old container and promote green
    /// to the canonical name; on failure remove green and leave the old container
    /// serving. Docker guarantees the green candidate's ephemeral host port never
    /// collides with the still-serving old container's, so the two always coexist.
    /// </summary>
    private async Task BlueGreenReplaceAsync(ReconcileAction action, CancellationToken ct)
    {
        var d = action.Desired!;
        var greenName = ReconcileNaming.GreenNameFor(d.Name);

        try
        {
            // Clear any stale green candidate from a previous crashed attempt.
            await _runtime.StopAndRemoveAsync(greenName, ct);

            // Docker assigns the green candidate a unique ephemeral host port; probe
            // and (later) route to that actual port.
            var greenPort = await StartWithStaleDigestFallbackAsync(d, greenName, ct);

            var greenAddress = _addressResolver.Resolve(greenName, greenPort);
            var ready = await _readinessProber.WaitForReadyAsync(
                greenAddress, _options.HealthPath, _options.ReadinessTimeout, ct);

            if (!ready)
            {
                // Abort cleanly: drop the unhealthy candidate; the old container
                // never stopped serving and no route changed.
                await _runtime.StopAndRemoveAsync(greenName, ct);
                await ReportOutcomeAsync(new ReconcileOutcome(
                    d.Name, action.Kind, ReconcileOutcomeStatus.Failed,
                    $"green candidate not ready within {_options.ReadinessTimeout}; old container left serving"), ct);
                return;
            }

            // Green is healthy: flip traffic, let the old destination drain, then
            // retire the old container and promote green to the canonical name.
            await _proxyState.SwapDestinationAsync(d.Name, greenAddress, ct);
            await Task.Delay(_options.DrainDelay, ct);
            await _runtime.StopAndRemoveAsync(d.Name, ct);
            await _runtime.RenameContainerAsync(greenName, d.Name, ct);
            // Docker port bindings are fixed at create time: the promoted container
            // keeps the host port Docker assigned it for the life of its process.
            // Record that as the canonical service's real port so clearing the swap
            // override routes back to where the container actually listens — not the
            // manifest port, which is container-internal only (tech-spec §7).
            _hostPorts.Set(d.Name, greenPort);
            await _proxyState.ClearDestinationOverrideAsync(d.Name, ct);

            // The green start may have created the CloudWatch group; set retention (§9).
            await EnsureRetentionAsync(d.Name, ct);
            await ReportOutcomeAsync(new ReconcileOutcome(
                d.Name, action.Kind, ReconcileOutcomeStatus.Succeeded, action.Reason), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Something threw mid-swap; best-effort remove the candidate and leave
            // the old container in place. Never rethrow into the loop.
            _logger.LogError(ex, "Blue-green replace of {Service} failed", d.Name);
            try
            {
                await _runtime.StopAndRemoveAsync(greenName, CancellationToken.None);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogError(cleanupEx, "Failed to clean up green candidate {Green}", greenName);
            }

            await ReportOutcomeAsync(new ReconcileOutcome(
                d.Name, action.Kind, ReconcileOutcomeStatus.Failed, ex.Message), CancellationToken.None);
        }
    }

    /// <summary>
    /// Pull <c>image:tag</c> (so the image is present locally), then start the
    /// container by the manifest's pinned digest when one is set, or by the freshly
    /// pulled digest otherwise. Returns the assigned host port.
    /// <para>
    /// Defense in depth (tech-spec §7): if starting by the pinned digest fails with
    /// image-not-found <b>after</b> a successful pull of <c>image:tag</c>, the pin is
    /// stale (production 2026-08-08: an Upsert changed the tag but left the old
    /// digest, so every start looped on <c>No such image ...@&lt;stale digest&gt;</c>).
    /// Fall back to the pulled digest for this start and log a warning naming the
    /// stale digest. The manifest's pinned digest is authoritative for drift
    /// detection only after a successful deploy; a one-off fallback start does not
    /// rewrite it — a deploy re-resolves it, and the Upsert fix now clears it up front.
    /// </para>
    /// </summary>
    private async Task<int> StartWithStaleDigestFallbackAsync(DesiredService d, string containerName, CancellationToken ct)
    {
        // Build the per-service log config (tech-spec §4.3): awslogs → CloudWatch group
        // /gateway/services/{d.Name}, stream = this instance's id. A green candidate
        // uses its service's group/stream, not the {name}-green container name, so its
        // logs land in the canonical group and survive promotion.
        var logConfig = await BuildLogConfigAsync(d.Name, ct);

        // Pull to ensure the image is present locally; prefer the manifest's pinned
        // digest, falling back to whatever the pull resolved.
        var pulled = await _runtime.PullImageAsync(d.Image, d.Tag, ct);
        var pinned = d.Digest;
        var digest = string.IsNullOrEmpty(pinned) ? pulled : pinned;

        try
        {
            return await _runtime.StartServiceContainerAsync(SpecFor(d, containerName, digest, logConfig), ct);
        }
        catch (ContainerImageNotFoundException ex)
            when (!string.IsNullOrEmpty(pinned) && !string.Equals(pinned, pulled, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                ex,
                "Start of {Container} by pinned digest {StaleDigest} failed with image-not-found after a "
                + "successful pull of {Image}:{Tag}; the pinned digest is stale. Falling back to the pulled "
                + "digest {PulledDigest} for this start.",
                containerName, pinned, d.Image, d.Tag, pulled);
            return await _runtime.StartServiceContainerAsync(SpecFor(d, containerName, pulled, logConfig), ct);
        }
    }

    /// <summary>
    /// Resolve the per-service <see cref="LogDriverConfig"/> for a container start
    /// (tech-spec §4.3). Uses this instance's id as the awslogs stream and its region
    /// (or <c>AWS_REGION</c>) for the driver region; honors the json-file escape hatch.
    /// </summary>
    private async Task<LogDriverConfig> BuildLogConfigAsync(string service, CancellationToken ct)
    {
        var identity = await _metadata.GetAsync(ct);
        return LogConfigFactory.ForService(service, identity.InstanceId, identity.Region, _options.LogDriver);
    }

    private ServiceContainerSpec SpecFor(DesiredService d, string containerName, string? digest, LogDriverConfig logConfig) =>
        new(
            Name: containerName,
            Image: d.Image,
            Digest: digest,
            Port: d.Port,
            EnvVars: d.EnvVars,
            Network: _options.Network,
            LogConfig: logConfig,
            EnvHash: d.EnvHash);

    /// <summary>
    /// Set 30-day retention on a service's CloudWatch log group once per group
    /// (tech-spec §9). Only meaningful for the awslogs driver (json-file has no
    /// group). Called after a container start that may have created the group via
    /// <c>awslogs-create-group</c>; runs on every instance (not leader-only) and
    /// never crashes the loop. AccessDenied is tolerated inside the admin (IAM may
    /// lag); any other failure forgets the mark so a later loop retries.
    /// </summary>
    private async Task EnsureRetentionAsync(string service, CancellationToken ct)
    {
        if (!LogConfigFactory.UsesAwsLogs(_options.LogDriver))
        {
            return;
        }

        var group = CloudWatchLogStore.LogGroupFor(service);
        lock (_retentionEnsured)
        {
            if (!_retentionEnsured.Add(group))
            {
                return;
            }
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var admin = scope.ServiceProvider.GetService<ILogGroupAdmin>();
            if (admin is null)
            {
                return;
            }

            await admin.EnsureRetentionAsync(group, LogConfigFactory.RetentionDays, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to set retention on log group {Group}; will retry next loop.", group);
            lock (_retentionEnsured)
            {
                _retentionEnsured.Remove(group);
            }
        }
    }

    private TimeSpan NextDelay()
    {
        // Full jitter up to MaxJitter so a fleet does not hit the registry/DB in
        // lockstep (tech-spec §4.3). Random.Shared is fine — jitter needs no
        // cryptographic quality and this is not on the tested path.
        var jitterMs = Random.Shared.NextDouble() * _options.MaxJitter.TotalMilliseconds;
        return _options.Interval + TimeSpan.FromMilliseconds(jitterMs);
    }
}
