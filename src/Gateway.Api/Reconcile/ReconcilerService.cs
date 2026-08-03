using Gateway.Api.Containers;
using Gateway.Api.Data;
using Gateway.Api.Instances;
using Gateway.Api.Manifest;
using Gateway.Api.Proxy;
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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadinessProber _readinessProber;
    private readonly IReconcileReporter _reporter;
    private readonly InstanceMetadataProvider _metadata;
    private readonly ILeaderElection _leaderElection;
    private readonly ReconcilerOptions _options;
    private readonly ILogger<ReconcilerService> _logger;

    public ReconcilerService(
        IContainerRuntime runtime,
        ProxyStateService proxyState,
        IServiceAddressResolver addressResolver,
        IServiceScopeFactory scopeFactory,
        IReadinessProber readinessProber,
        IReconcileReporter reporter,
        InstanceMetadataProvider metadata,
        ILeaderElection leaderElection,
        ReconcilerOptions options,
        ILogger<ReconcilerService> logger)
    {
        _runtime = runtime;
        _proxyState = proxyState;
        _addressResolver = addressResolver;
        _scopeFactory = scopeFactory;
        _readinessProber = readinessProber;
        _reporter = reporter;
        _metadata = metadata;
        _leaderElection = leaderElection;
        _options = options;
        _logger = logger;
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
        // Determine leadership up front: it is re-verified every loop and gates the
        // fleet-wide duties in the heartbeat step below.
        var isLeader = await TryAcquireLeadershipAsync(ct);

        var desired = await BuildDesiredAsync(ct);
        var actual = await _runtime.ListManagedContainersAsync(ct);
        var plan = ReconcilePlanner.Plan(desired, actual);

        foreach (var action in plan)
        {
            ct.ThrowIfCancellationRequested();
            await ExecuteAsync(action, ct);
        }

        // After converging, publish this instance's heartbeat + inventory so any
        // instance can answer fleet-wide queries (tech-spec §4.3, §4.4).
        await HeartbeatAsync(isLeader, ct);
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
                Services = InstanceServicesJson.Build(containers),
                HeartbeatAt = DateTimeOffset.UtcNow,
            };

            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IInstanceStatusStore>();

            await store.UpsertAsync(status, ct);

            if (isLeader)
            {
                var cutoff = DateTimeOffset.UtcNow - _options.InstanceStaleThreshold;
                var pruned = await store.DeleteStaleAsync(cutoff, ct);
                if (pruned > 0)
                {
                    _logger.LogInformation(
                        "Leader pruned {Count} stale instance_status row(s) older than {Threshold}.",
                        pruned, _options.InstanceStaleThreshold);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A heartbeat failure must not crash the loop; the next tick retries.
            _logger.LogError(ex, "Failed to publish instance_status heartbeat");
        }
    }

    private async Task<IReadOnlyList<DesiredService>> BuildDesiredAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IManifestStore>();
        var envProvider = scope.ServiceProvider.GetRequiredService<IServiceEnvProvider>();

        var manifests = await store.GetAllAsync(ct);
        var desired = new List<DesiredService>(manifests.Count);
        foreach (var m in manifests)
        {
            var env = await envProvider.GetEnvAsync(m, ct);
            desired.Add(new DesiredService(
                Name: m.Name,
                Image: m.Image,
                Tag: m.Tag,
                Digest: m.Digest,
                Port: m.Port,
                DesiredStatus: m.DesiredStatus,
                EnvHash: EnvHasher.Compute(env),
                EnvVars: env));
        }

        return desired;
    }

    private Task ExecuteAsync(ReconcileAction action, CancellationToken ct) => action.Kind switch
    {
        ReconcileActionKind.Start => StartAsync(action, ct),
        ReconcileActionKind.StopRemove => StopRemoveAsync(action, ct),
        ReconcileActionKind.BlueGreenReplace => BlueGreenReplaceAsync(action, ct),
        _ => Task.CompletedTask,
    };

    private async Task StartAsync(ReconcileAction action, CancellationToken ct)
    {
        var d = action.Desired!;
        try
        {
            var digest = await ResolveDigestAsync(d, ct);
            await _runtime.StartServiceContainerAsync(SpecFor(d, d.Name, sidePort: null, digest), ct);
            await _proxyState.RefreshRoutesAsync(ct);
            await _reporter.ReportAsync(new ReconcileOutcome(
                d.Name, action.Kind, ReconcileOutcomeStatus.Succeeded, action.Reason), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to start {Service}", d.Name);
            await _reporter.ReportAsync(new ReconcileOutcome(
                d.Name, action.Kind, ReconcileOutcomeStatus.Failed, ex.Message), ct);
        }
    }

    private async Task StopRemoveAsync(ReconcileAction action, CancellationToken ct)
    {
        try
        {
            await _runtime.StopAndRemoveAsync(action.ServiceName, ct);
            await _proxyState.RefreshRoutesAsync(ct);
            await _reporter.ReportAsync(new ReconcileOutcome(
                action.ServiceName, action.Kind, ReconcileOutcomeStatus.Succeeded, action.Reason), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to stop/remove {Service}", action.ServiceName);
            await _reporter.ReportAsync(new ReconcileOutcome(
                action.ServiceName, action.Kind, ReconcileOutcomeStatus.Failed, ex.Message), ct);
        }
    }

    /// <summary>
    /// Zero-downtime replace (tech-spec §7): pull, start <c>{name}-green</c> on a
    /// side port, poll readiness; on success swap the YARP destination to green,
    /// drain, then remove the old container and promote green to the canonical
    /// name; on failure remove green and leave the old container serving.
    /// </summary>
    private async Task BlueGreenReplaceAsync(ReconcileAction action, CancellationToken ct)
    {
        var d = action.Desired!;
        var greenName = ReconcileNaming.GreenNameFor(d.Name);
        var sidePort = d.Port + _options.SidePortOffset;

        try
        {
            // Clear any stale green candidate from a previous crashed attempt.
            await _runtime.StopAndRemoveAsync(greenName, ct);

            var digest = await ResolveDigestAsync(d, ct);
            await _runtime.StartServiceContainerAsync(SpecFor(d, greenName, sidePort, digest), ct);

            var greenAddress = _addressResolver.Resolve(greenName, sidePort);
            var ready = await _readinessProber.WaitForReadyAsync(
                greenAddress, _options.HealthPath, _options.ReadinessTimeout, ct);

            if (!ready)
            {
                // Abort cleanly: drop the unhealthy candidate; the old container
                // never stopped serving and no route changed.
                await _runtime.StopAndRemoveAsync(greenName, ct);
                await _reporter.ReportAsync(new ReconcileOutcome(
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
            await _proxyState.ClearDestinationOverrideAsync(d.Name, ct);

            await _reporter.ReportAsync(new ReconcileOutcome(
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

            await _reporter.ReportAsync(new ReconcileOutcome(
                d.Name, action.Kind, ReconcileOutcomeStatus.Failed, ex.Message), CancellationToken.None);
        }
    }

    private async Task<string?> ResolveDigestAsync(DesiredService d, CancellationToken ct)
    {
        // Pull to ensure the image is present locally; prefer the manifest's pinned
        // digest, falling back to whatever the pull resolved.
        var pulled = await _runtime.PullImageAsync(d.Image, d.Tag, ct);
        return string.IsNullOrEmpty(d.Digest) ? pulled : d.Digest;
    }

    private ServiceContainerSpec SpecFor(DesiredService d, string containerName, int? sidePort, string? digest) =>
        new(
            Name: containerName,
            Image: d.Image,
            Digest: digest,
            Port: d.Port,
            SidePort: sidePort,
            EnvVars: d.EnvVars,
            Network: _options.Network,
            LogConfig: _options.LogDriver,
            EnvHash: d.EnvHash);

    private TimeSpan NextDelay()
    {
        // Full jitter up to MaxJitter so a fleet does not hit the registry/DB in
        // lockstep (tech-spec §4.3). Random.Shared is fine — jitter needs no
        // cryptographic quality and this is not on the tested path.
        var jitterMs = Random.Shared.NextDouble() * _options.MaxJitter.TotalMilliseconds;
        return _options.Interval + TimeSpan.FromMilliseconds(jitterMs);
    }
}
