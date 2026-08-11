using Gateway.Api.Containers;
using Gateway.Api.Data;
using Gateway.Api.Instances;
using Gateway.Api.Management;
using Gateway.Api.Manifest;
using Gateway.Api.Proxy;
using Gateway.Api.RealTime;
using Gateway.Api.Reconcile;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Api.Tests;

/// <summary>
/// Tests the reconciler's deploy-progress loop (tech-spec §4.5, §7): each instance
/// upserts its own <c>deploy_instance_status</c> once it is running a deploy's target
/// digest, and the leader marks the <c>deploy_history</c> row done once every live
/// instance has converged (or leaves it in progress while a straggler remains).
/// </summary>
public class ReconcilerDeployProgressTests
{
    private static readonly string EmptyEnvHash = EnvHasher.Compute(new Dictionary<string, string>());

    private sealed class FakeReadinessProber : IReadinessProber
    {
        public Task<bool> WaitForReadyAsync(string address, string healthPath, TimeSpan timeout, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    /// <summary>A prober that never reports ready — models a green candidate that fails
    /// its health check, so a blue-green replace aborts and reports Failed.</summary>
    private sealed class FailingReadinessProber : IReadinessProber
    {
        public Task<bool> WaitForReadyAsync(string address, string healthPath, TimeSpan timeout, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    private sealed class NullEnvProvider : IServiceEnvProvider
    {
        public Task<IReadOnlyDictionary<string, string>> GetEnvAsync(ServiceManifest manifest, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    }

    private sealed class NullReporter : IReconcileReporter
    {
        public Task ReportAsync(ReconcileOutcome outcome, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A published envelope: the channel, event name, and its anonymous data object.</summary>
    private sealed record Published(string Channel, string Event, object Data)
    {
        /// <summary>Read a property off the anonymous <c>data</c> object by name.</summary>
        public object? Get(string name) => Data.GetType().GetProperty(name)?.GetValue(Data);
    }

    /// <summary>Records every <c>TryPublish</c> so a test can assert the envelope shape.</summary>
    private sealed class RecordingPublisher : IChannelEventPublisher
    {
        public List<Published> Published { get; } = new();

        public Task PublishAsync(string channel, string @event, object data, CancellationToken ct = default) =>
            Task.CompletedTask;

        public void TryPublish(string channel, string @event, object data) =>
            Published.Add(new Published(channel, @event, data));
    }

    private sealed class Harness
    {
        public InMemoryManifestStore Store { get; } = new();
        public FakeContainerRuntime Runtime { get; } = new();
        public FakeInstanceStatusStore StatusStore { get; } = new();
        public FakeDeployStore DeployStore { get; } = new();
        public IChannelEventPublisher Publisher { get; }
        public ReconcilerService Service { get; }

        public Harness(
            bool isLeader = true,
            IChannelEventPublisher? publisher = null,
            IReadinessProber? readiness = null,
            TimeSpan? deployTimeout = null)
        {
            Publisher = publisher ?? new RecordingPublisher();
            var options = new ReconcilerOptions
            {
                Enabled = true,
                DrainDelay = TimeSpan.Zero,
                ReadinessTimeout = TimeSpan.FromMilliseconds(10),
                ReadinessPollInterval = TimeSpan.FromMilliseconds(1),
                DeployTimeout = deployTimeout ?? TimeSpan.FromMinutes(10),
            };

            var services = new ServiceCollection();
            services.AddSingleton<IManifestStore>(Store);
            services.AddSingleton<IServiceEnvProvider, NullEnvProvider>();
            services.AddSingleton<IInstanceStatusStore>(StatusStore);
            services.AddSingleton<IDeployStore>(DeployStore);
            services.AddSingleton<IServiceAddressResolver, HostLoopbackAddressResolver>();
            services.AddSingleton<ServiceHostPortMap>();
            services.AddSingleton<ManifestProxyConfigProvider>();
            services.AddSingleton<ProxyStateService>();
            var provider = services.BuildServiceProvider();

            var metadata = new InstanceMetadataProvider(new IInstanceMetadata[]
            {
                new StubInstanceMetadata(new InstanceIdentity("i-test", "10.0.0.9", null)),
            });

            Service = new ReconcilerService(
                Runtime,
                provider.GetRequiredService<ProxyStateService>(),
                provider.GetRequiredService<IServiceAddressResolver>(),
                provider.GetRequiredService<ServiceHostPortMap>(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                readiness ?? new FakeReadinessProber(),
                new NullReporter(),
                metadata,
                new InMemoryLeaderElection(isLeader),
                options,
                NullLogger<ReconcilerService>.Instance,
                migrationGate: null,
                publisher: Publisher);
        }
    }

    private static ServiceManifest Manifest(string name, string digest) => new()
    {
        Name = name,
        Image = $"registry/{name}",
        Tag = "latest",
        Digest = digest,
        Port = 8080,
        DesiredStatus = "running",
        IncludeInHealth = true,
        UpdatedBy = "test",
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static ContainerInfo Running(string name, string digest) =>
        new(name, $"registry/{name}", digest, "running", DateTimeOffset.UnixEpoch, EmptyEnvHash);

    [Fact]
    public async Task Leader_MarksDeployDone_WhenLiveFleetConverged()
    {
        var harness = new Harness(isLeader: true);
        harness.Runtime.Seed(Running("svc-a", "sha256:v2"));
        await harness.Store.UpsertAsync(Manifest("svc-a", "sha256:v2"));
        await harness.DeployStore.AddAsync(new DeployHistory
        {
            Service = "svc-a", ToDigest = "sha256:v2", Actor = "bob",
            Action = DeployAction.Deploy, Status = DeployStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        });

        await harness.Service.RunOnceAsync();

        // This instance reported its convergence...
        var progress = Assert.Single(harness.DeployStore.InstanceStatuses);
        Assert.Equal("i-test", progress.InstanceId);
        Assert.Equal(DeployInstanceState.Converged, progress.Status);

        // ...and the leader closed the deploy (whole live fleet on the target digest).
        var deploy = Assert.Single(harness.DeployStore.History);
        Assert.Equal(DeployStatus.Done, deploy.Status);
        Assert.NotNull(deploy.FinishedAt);
    }

    [Fact]
    public async Task Leader_LeavesInProgress_WhileStragglerRemains()
    {
        var harness = new Harness(isLeader: true);
        harness.Runtime.Seed(Running("svc-a", "sha256:v2"));
        await harness.Store.UpsertAsync(Manifest("svc-a", "sha256:v2"));
        // A second live instance still on the old digest.
        harness.StatusStore.Seed(ManagementTestData.Instance(
            "i-2", heartbeatAt: DateTimeOffset.UtcNow, running: ("svc-a", "sha256:v1")));
        await harness.DeployStore.AddAsync(new DeployHistory
        {
            Service = "svc-a", ToDigest = "sha256:v2", Actor = "bob",
            Action = DeployAction.Deploy, Status = DeployStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        });

        await harness.Service.RunOnceAsync();

        var deploy = Assert.Single(harness.DeployStore.History);
        Assert.Equal(DeployStatus.InProgress, deploy.Status);
    }

    [Fact]
    public async Task NonLeader_ReportsOwnConvergence_ButNeverCompletesDeploy()
    {
        var harness = new Harness(isLeader: false);
        harness.Runtime.Seed(Running("svc-a", "sha256:v2"));
        await harness.Store.UpsertAsync(Manifest("svc-a", "sha256:v2"));
        await harness.DeployStore.AddAsync(new DeployHistory
        {
            Service = "svc-a", ToDigest = "sha256:v2", Actor = "bob",
            Action = DeployAction.Deploy, Status = DeployStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        });

        await harness.Service.RunOnceAsync();

        Assert.Single(harness.DeployStore.InstanceStatuses);
        var deploy = Assert.Single(harness.DeployStore.History);
        Assert.Equal(DeployStatus.InProgress, deploy.Status);
    }

    [Fact]
    public async Task NotConverged_Instance_DoesNotReport()
    {
        var harness = new Harness(isLeader: true);
        // This instance still runs the old digest; the deploy targets v2.
        harness.Runtime.Seed(Running("svc-a", "sha256:v1"));
        await harness.Store.UpsertAsync(Manifest("svc-a", "sha256:v1"));
        await harness.DeployStore.AddAsync(new DeployHistory
        {
            Service = "svc-a", ToDigest = "sha256:v2", Actor = "bob",
            Action = DeployAction.Deploy, Status = DeployStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        });

        await harness.Service.RunOnceAsync();

        Assert.Empty(harness.DeployStore.InstanceStatuses);
        var deploy = Assert.Single(harness.DeployStore.History);
        Assert.Equal(DeployStatus.InProgress, deploy.Status);
    }

    [Fact]
    public async Task MarkDeploy_PublishesTerminalDeployEnvelope_WhenFleetConverged()
    {
        var recorder = new RecordingPublisher();
        var harness = new Harness(isLeader: true, publisher: recorder);
        harness.Runtime.Seed(Running("svc-a", "sha256:v2"));
        await harness.Store.UpsertAsync(Manifest("svc-a", "sha256:v2"));
        await harness.DeployStore.AddAsync(new DeployHistory
        {
            Service = "svc-a", FromDigest = "sha256:v1", ToDigest = "sha256:v2", Actor = "bob",
            Action = DeployAction.Deploy, Status = DeployStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        });

        await harness.Service.RunOnceAsync();

        var deploy = Assert.Single(harness.DeployStore.History);
        Assert.Equal(DeployStatus.Done, deploy.Status);

        // The terminal "deploy" event carries the full contract shape with the terminal
        // status, an ISO finishedAt, and a null error (the deploy did not fail).
        var terminal = Assert.Single(
            recorder.Published.Where(p => p.Event == "deploy"));
        Assert.Equal(ManagementEndpoints.OpsDeploysChannel, terminal.Channel);
        Assert.Equal(deploy.Id, terminal.Get("deployId"));
        Assert.Equal("svc-a", terminal.Get("service"));
        Assert.Equal(DeployAction.Deploy, terminal.Get("action"));
        Assert.Equal("sha256:v1", terminal.Get("fromDigest"));
        Assert.Equal("sha256:v2", terminal.Get("toDigest"));
        Assert.Equal(DeployStatus.Done, terminal.Get("status"));
        Assert.Null(terminal.Get("error"));
        var finishedAt = Assert.IsType<string>(terminal.Get("finishedAt"));
        Assert.Equal(deploy.FinishedAt!.Value, DateTimeOffset.Parse(finishedAt));
    }

    [Fact]
    public async Task Convergence_PublishesDeployInstanceEnvelope()
    {
        // A straggler keeps the deploy in progress, so the only "deploy" here is none —
        // this isolates the per-instance convergence broadcast.
        var recorder = new RecordingPublisher();
        var harness = new Harness(isLeader: true, publisher: recorder);
        harness.Runtime.Seed(Running("svc-a", "sha256:v2"));
        await harness.Store.UpsertAsync(Manifest("svc-a", "sha256:v2"));
        harness.StatusStore.Seed(ManagementTestData.Instance(
            "i-2", heartbeatAt: DateTimeOffset.UtcNow, running: ("svc-a", "sha256:v1")));
        await harness.DeployStore.AddAsync(new DeployHistory
        {
            Service = "svc-a", ToDigest = "sha256:v2", Actor = "bob",
            Action = DeployAction.Deploy, Status = DeployStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        });

        await harness.Service.RunOnceAsync();

        var deploy = Assert.Single(harness.DeployStore.History);
        var instanceEvent = Assert.Single(
            recorder.Published.Where(p => p.Event == "deployInstance"));
        Assert.Equal(ManagementEndpoints.OpsDeploysChannel, instanceEvent.Channel);
        Assert.Equal(deploy.Id, instanceEvent.Get("deployId"));
        Assert.Equal("i-test", instanceEvent.Get("instanceId"));
        Assert.Equal(DeployInstanceState.Converged, instanceEvent.Get("status"));
        Assert.Null(instanceEvent.Get("error"));
    }

    [Fact]
    public async Task ThrowingPublisher_DoesNotBreakDeployProgress()
    {
        // The REAL publisher over a hub that always throws — a backplane outage on the
        // leader's reconcile path. TryPublish swallows it, so the reconcile is untouched.
        var throwingPublisher = new ChannelEventPublisher(
            new FakeGatewayHubContext { SendError = new InvalidOperationException("backplane down") },
            NullLogger<ChannelEventPublisher>.Instance);
        var harness = new Harness(isLeader: true, publisher: throwingPublisher);
        harness.Runtime.Seed(Running("svc-a", "sha256:v2"));
        await harness.Store.UpsertAsync(Manifest("svc-a", "sha256:v2"));
        await harness.DeployStore.AddAsync(new DeployHistory
        {
            Service = "svc-a", ToDigest = "sha256:v2", Actor = "bob",
            Action = DeployAction.Deploy, Status = DeployStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        });

        // A publisher that throws on every TryPublish must not disturb the reconcile:
        // the convergence row is still written and the deploy is still marked done.
        await harness.Service.RunOnceAsync();

        var progress = Assert.Single(harness.DeployStore.InstanceStatuses);
        Assert.Equal(DeployInstanceState.Converged, progress.Status);
        var deploy = Assert.Single(harness.DeployStore.History);
        Assert.Equal(DeployStatus.Done, deploy.Status);
    }

    [Fact]
    public async Task InstanceFailure_MarksDeployFailed_WhenNoInstanceConverged()
    {
        // The one live instance's blue-green replace fails its health check, so no
        // instance ever reaches the target digest — the deploy is terminal "failed".
        var recorder = new RecordingPublisher();
        var harness = new Harness(isLeader: true, publisher: recorder, readiness: new FailingReadinessProber());
        harness.Runtime.Seed(Running("svc-a", "sha256:v1"));
        await harness.Store.UpsertAsync(Manifest("svc-a", "sha256:v2"));
        await harness.DeployStore.AddAsync(new DeployHistory
        {
            Service = "svc-a", FromDigest = "sha256:v1", ToDigest = "sha256:v2", Actor = "bob",
            Action = DeployAction.Deploy, Status = DeployStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        });

        await harness.Service.RunOnceAsync();

        // The failed blue-green replace wrote a Failed per-instance row correlated to the deploy...
        var progress = Assert.Single(harness.DeployStore.InstanceStatuses);
        Assert.Equal("i-test", progress.InstanceId);
        Assert.Equal(DeployInstanceState.Failed, progress.Status);
        Assert.False(string.IsNullOrEmpty(progress.Detail));

        // ...and the leader marked the whole deploy failed (nobody converged), with an error.
        var deploy = Assert.Single(harness.DeployStore.History);
        Assert.Equal(DeployStatus.Failed, deploy.Status);
        Assert.NotNull(deploy.FinishedAt);

        // The per-instance failure was broadcast with status "failed" and an error...
        var instanceEvent = Assert.Single(recorder.Published.Where(p => p.Event == "deployInstance"));
        Assert.Equal(DeployInstanceState.Failed, instanceEvent.Get("status"));
        Assert.Equal("i-test", instanceEvent.Get("instanceId"));
        Assert.NotNull(instanceEvent.Get("error"));

        // ...and the terminal "deploy" event carries the failed status + a non-null error.
        var terminal = Assert.Single(recorder.Published.Where(p => p.Event == "deploy"));
        Assert.Equal(DeployStatus.Failed, terminal.Get("status"));
        Assert.NotNull(terminal.Get("error"));
        Assert.IsType<string>(terminal.Get("finishedAt"));
    }

    [Fact]
    public async Task InstanceFailure_MarksDeployPartial_WhenAnotherInstanceConverged()
    {
        // This instance's blue-green replace fails, but a second live instance is already
        // on the target digest — so the deploy is "partial", not "failed".
        var recorder = new RecordingPublisher();
        var harness = new Harness(isLeader: true, publisher: recorder, readiness: new FailingReadinessProber());
        harness.Runtime.Seed(Running("svc-a", "sha256:v1"));
        await harness.Store.UpsertAsync(Manifest("svc-a", "sha256:v2"));
        harness.StatusStore.Seed(ManagementTestData.Instance(
            "i-2", heartbeatAt: DateTimeOffset.UtcNow, running: ("svc-a", "sha256:v2")));
        await harness.DeployStore.AddAsync(new DeployHistory
        {
            Service = "svc-a", ToDigest = "sha256:v2", Actor = "bob",
            Action = DeployAction.Deploy, Status = DeployStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        });

        await harness.Service.RunOnceAsync();

        var progress = Assert.Single(harness.DeployStore.InstanceStatuses);
        Assert.Equal(DeployInstanceState.Failed, progress.Status);

        var deploy = Assert.Single(harness.DeployStore.History);
        Assert.Equal(DeployStatus.Partial, deploy.Status);
        Assert.NotNull(deploy.FinishedAt);

        var terminal = Assert.Single(recorder.Published.Where(p => p.Event == "deploy"));
        Assert.Equal(DeployStatus.Partial, terminal.Get("status"));
        Assert.NotNull(terminal.Get("error"));
    }

    [Fact]
    public async Task Leader_TimesOutStaleDeploy_MarksFailed_WhenNoneConverged()
    {
        // A deploy older than the timeout whose target digest nobody runs: the leader
        // fails it with "deploy timed out" instead of rescanning it forever.
        var recorder = new RecordingPublisher();
        var harness = new Harness(
            isLeader: true, publisher: recorder, deployTimeout: TimeSpan.FromMinutes(10));
        // Fleet is fully converged on v1; the deploy targets an unreachable v2.
        harness.Runtime.Seed(Running("svc-a", "sha256:v1"));
        await harness.Store.UpsertAsync(Manifest("svc-a", "sha256:v1"));
        await harness.DeployStore.AddAsync(new DeployHistory
        {
            Service = "svc-a", ToDigest = "sha256:v2", Actor = "bob",
            Action = DeployAction.Deploy, Status = DeployStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20),
        });

        await harness.Service.RunOnceAsync();

        var deploy = Assert.Single(harness.DeployStore.History);
        Assert.Equal(DeployStatus.Failed, deploy.Status);
        Assert.NotNull(deploy.FinishedAt);

        var terminal = Assert.Single(recorder.Published.Where(p => p.Event == "deploy"));
        Assert.Equal(DeployStatus.Failed, terminal.Get("status"));
        Assert.Equal("deploy timed out", terminal.Get("error"));
    }

    [Fact]
    public async Task Leader_TimesOutStaleDeploy_PersistsDetail_ViaStore()
    {
        // Task #608 finding 5: the terminal failure reason must be PERSISTED, not just ride
        // the fire-and-forget event — GET /mgmt/deploys reads the store, so a timed-out
        // deploy must have a non-null Detail there (events are hints, the store is truth).
        var harness = new Harness(isLeader: true, deployTimeout: TimeSpan.FromMinutes(10));
        harness.Runtime.Seed(Running("svc-a", "sha256:v1"));
        await harness.Store.UpsertAsync(Manifest("svc-a", "sha256:v1"));
        await harness.DeployStore.AddAsync(new DeployHistory
        {
            Service = "svc-a", ToDigest = "sha256:v2", Actor = "bob",
            Action = DeployAction.Deploy, Status = DeployStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20),
        });

        await harness.Service.RunOnceAsync();

        var deploy = Assert.Single(harness.DeployStore.History);
        Assert.Equal(DeployStatus.Failed, deploy.Status);
        // The reason is persisted on the stored row, not only broadcast — wrapped in a
        // JSON object because deploy_history.detail is jsonb on Postgres: a bare string
        // is invalid jsonb and would throw 22P02 in production, which Sqlite-backed
        // tests cannot catch (review finding), so this asserts the exact stored JSON.
        Assert.Equal("""{"error":"deploy timed out"}""", deploy.Detail);
    }

    [Fact]
    public async Task Leader_TimesOutStaleDeploy_MarksPartial_WhenSomeConverged()
    {
        // Some of the fleet converged before the deploy went stale: timeout → "partial".
        var harness = new Harness(isLeader: true, deployTimeout: TimeSpan.FromMinutes(10));
        harness.Runtime.Seed(Running("svc-a", "sha256:v2"));
        await harness.Store.UpsertAsync(Manifest("svc-a", "sha256:v2"));
        // A straggler that never converged keeps the fleet from being all-converged.
        harness.StatusStore.Seed(ManagementTestData.Instance(
            "i-2", heartbeatAt: DateTimeOffset.UtcNow, running: ("svc-a", "sha256:v1")));
        await harness.DeployStore.AddAsync(new DeployHistory
        {
            Service = "svc-a", ToDigest = "sha256:v2", Actor = "bob",
            Action = DeployAction.Deploy, Status = DeployStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20),
        });

        await harness.Service.RunOnceAsync();

        var deploy = Assert.Single(harness.DeployStore.History);
        Assert.Equal(DeployStatus.Partial, deploy.Status);
        Assert.NotNull(deploy.FinishedAt);
    }

    [Fact]
    public async Task Leader_DoesNotTimeOut_FreshDeploy_WithStraggler()
    {
        // The same shape as the partial-timeout case but the deploy is fresh: it must
        // stay in progress — the timeout must not fire early.
        var harness = new Harness(isLeader: true, deployTimeout: TimeSpan.FromMinutes(10));
        harness.Runtime.Seed(Running("svc-a", "sha256:v2"));
        await harness.Store.UpsertAsync(Manifest("svc-a", "sha256:v2"));
        harness.StatusStore.Seed(ManagementTestData.Instance(
            "i-2", heartbeatAt: DateTimeOffset.UtcNow, running: ("svc-a", "sha256:v1")));
        await harness.DeployStore.AddAsync(new DeployHistory
        {
            Service = "svc-a", ToDigest = "sha256:v2", Actor = "bob",
            Action = DeployAction.Deploy, Status = DeployStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        });

        await harness.Service.RunOnceAsync();

        var deploy = Assert.Single(harness.DeployStore.History);
        Assert.Equal(DeployStatus.InProgress, deploy.Status);
        Assert.Null(deploy.FinishedAt);
    }
}
