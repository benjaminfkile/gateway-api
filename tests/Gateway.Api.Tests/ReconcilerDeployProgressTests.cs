using Gateway.Api.Containers;
using Gateway.Api.Data;
using Gateway.Api.Instances;
using Gateway.Api.Management;
using Gateway.Api.Manifest;
using Gateway.Api.Proxy;
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

    private sealed class NullEnvProvider : IServiceEnvProvider
    {
        public Task<IReadOnlyDictionary<string, string>> GetEnvAsync(ServiceManifest manifest, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    }

    private sealed class NullReporter : IReconcileReporter
    {
        public Task ReportAsync(ReconcileOutcome outcome, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Harness
    {
        public InMemoryManifestStore Store { get; } = new();
        public FakeContainerRuntime Runtime { get; } = new();
        public FakeInstanceStatusStore StatusStore { get; } = new();
        public FakeDeployStore DeployStore { get; } = new();
        public ReconcilerService Service { get; }

        public Harness(bool isLeader = true)
        {
            var options = new ReconcilerOptions
            {
                Enabled = true,
                DrainDelay = TimeSpan.Zero,
                ReadinessTimeout = TimeSpan.FromMilliseconds(10),
                ReadinessPollInterval = TimeSpan.FromMilliseconds(1),
            };

            var services = new ServiceCollection();
            services.AddSingleton<IManifestStore>(Store);
            services.AddSingleton<IServiceEnvProvider, NullEnvProvider>();
            services.AddSingleton<IInstanceStatusStore>(StatusStore);
            services.AddSingleton<IDeployStore>(DeployStore);
            services.AddSingleton<IServiceAddressResolver, HostLoopbackAddressResolver>();
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
                provider.GetRequiredService<IServiceScopeFactory>(),
                new FakeReadinessProber(),
                new NullReporter(),
                metadata,
                new InMemoryLeaderElection(isLeader),
                options,
                NullLogger<ReconcilerService>.Instance);
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
}
