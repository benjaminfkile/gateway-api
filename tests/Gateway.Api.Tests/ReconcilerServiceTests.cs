using Gateway.Api.Containers;
using Gateway.Api.Data;
using Gateway.Api.Instances;
using Gateway.Api.Manifest;
using Gateway.Api.Proxy;
using Gateway.Api.Reconcile;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Api.Tests;

/// <summary>
/// Tests the <see cref="ReconcilerService"/> and the blue-green deploy flow
/// against a <see cref="FakeContainerRuntime"/> — no real Docker. Covers the
/// env-flag gate, plain start/stop convergence, a successful zero-downtime
/// replace (old keeps serving until green is healthy), and the failure-abort path
/// (green removed, old left serving).
/// </summary>
public class ReconcilerServiceTests
{
    private static readonly string EmptyEnvHash =
        EnvHasher.Compute(new Dictionary<string, string>());

    /// <summary>Readiness prober with a controllable verdict and a probe-time hook.</summary>
    private sealed class FakeReadinessProber : IReadinessProber
    {
        public bool Ready { get; set; } = true;
        public List<string> ProbedAddresses { get; } = new();

        /// <summary>Runs at probe time — used to assert state while green is being checked.</summary>
        public Action? OnProbe { get; set; }

        public Task<bool> WaitForReadyAsync(string address, string healthPath, TimeSpan timeout, CancellationToken ct = default)
        {
            ProbedAddresses.Add(address);
            OnProbe?.Invoke();
            return Task.FromResult(Ready);
        }
    }

    private sealed class FakeReporter : IReconcileReporter
    {
        public List<ReconcileOutcome> Outcomes { get; } = new();

        public Task ReportAsync(ReconcileOutcome outcome, CancellationToken ct = default)
        {
            Outcomes.Add(outcome);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEnvProvider : IServiceEnvProvider
    {
        private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _byService =
            new(StringComparer.Ordinal);

        public void Set(string service, IReadOnlyDictionary<string, string> env) => _byService[service] = env;

        public Task<IReadOnlyDictionary<string, string>> GetEnvAsync(ServiceManifest manifest, CancellationToken ct = default) =>
            Task.FromResult(_byService.TryGetValue(manifest.Name, out var env)
                ? env
                : new Dictionary<string, string>());
    }

    /// <summary>Assembles a reconciler wired to fakes plus a real ProxyStateService.</summary>
    private sealed class Harness
    {
        public InMemoryManifestStore Store { get; } = new();
        public FakeContainerRuntime Runtime { get; } = new();
        public FakeReadinessProber Prober { get; } = new();
        public FakeReporter Reporter { get; } = new();
        public FakeEnvProvider EnvProvider { get; } = new();
        public FakeInstanceStatusStore StatusStore { get; } = new();
        public InMemoryLeaderElection Leader { get; }
        public ReconcilerOptions Options { get; }
        public ReconcilerService Service { get; }

        public Harness(bool enabled = true, bool isLeader = true)
        {
            Leader = new InMemoryLeaderElection(isLeader);
            Options = new ReconcilerOptions
            {
                Enabled = enabled,
                // Keep the tests fast: no real drains or poll waits.
                DrainDelay = TimeSpan.Zero,
                ReadinessTimeout = TimeSpan.FromMilliseconds(50),
                ReadinessPollInterval = TimeSpan.FromMilliseconds(1),
            };

            var services = new ServiceCollection();
            services.AddSingleton<IManifestStore>(Store);
            services.AddSingleton<IServiceEnvProvider>(EnvProvider);
            services.AddSingleton<IInstanceStatusStore>(StatusStore);
            services.AddSingleton<IServiceAddressResolver, ContainerDnsAddressResolver>();
            services.AddSingleton<ManifestProxyConfigProvider>();
            services.AddSingleton<ProxyStateService>();
            var provider = services.BuildServiceProvider();

            var metadata = new InstanceMetadataProvider(
                new IInstanceMetadata[]
                {
                    new StubInstanceMetadata(new InstanceIdentity("i-test", "10.0.0.9", "203.0.113.9")),
                });

            Service = new ReconcilerService(
                Runtime,
                provider.GetRequiredService<ProxyStateService>(),
                provider.GetRequiredService<IServiceAddressResolver>(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                Prober,
                Reporter,
                metadata,
                Leader,
                Options,
                NullLogger<ReconcilerService>.Instance);
        }
    }

    private static ServiceManifest Manifest(
        string name,
        string status = "running",
        string? digest = "sha256:v1",
        int port = 8080) => new()
    {
        Name = name,
        Image = $"registry/{name}",
        Tag = "latest",
        Digest = digest,
        Port = port,
        DesiredStatus = status,
        IncludeInHealth = true,
        UpdatedBy = "test",
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static ContainerInfo Running(string name, string digest, string? envHash) =>
        new(name, $"registry/{name}", digest, "running", DateTimeOffset.UnixEpoch, envHash);

    [Fact]
    public async Task DisabledByDefault_DoesNothing()
    {
        var harness = new Harness(enabled: false);
        await harness.Store.UpsertAsync(Manifest("svc-a"));

        // BackgroundService.StartAsync invokes ExecuteAsync, which must bail out.
        await harness.Service.StartAsync(CancellationToken.None);
        await harness.Service.StopAsync(CancellationToken.None);

        Assert.Empty(harness.Runtime.Operations);
        Assert.False(harness.Runtime.Exists("svc-a"));
    }

    [Fact]
    public async Task Start_CreatesContainer_WhenNoneRunning()
    {
        var harness = new Harness();
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1"));

        await harness.Service.RunOnceAsync();

        Assert.True(harness.Runtime.Exists("svc-a"));
        var container = harness.Runtime.Get("svc-a")!;
        Assert.Equal("sha256:v1", container.Digest);
        Assert.Contains(harness.Runtime.Operations, op => op == "Pull:registry/svc-a:latest");
        var outcome = Assert.Single(harness.Reporter.Outcomes);
        Assert.Equal(ReconcileActionKind.Start, outcome.Kind);
        Assert.Equal(ReconcileOutcomeStatus.Succeeded, outcome.Status);
    }

    [Fact]
    public async Task Start_UsesResolvedDigest_WhenManifestDigestNull()
    {
        var harness = new Harness();
        harness.Runtime.PullDigest = (image, tag) => "sha256:resolved";
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: null));

        await harness.Service.RunOnceAsync();

        Assert.Equal("sha256:resolved", harness.Runtime.Get("svc-a")!.Digest);
    }

    [Fact]
    public async Task StopRemove_WhenDesiredStopped()
    {
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash));
        await harness.Store.UpsertAsync(Manifest("svc-a", status: "stopped"));

        await harness.Service.RunOnceAsync();

        Assert.False(harness.Runtime.Exists("svc-a"));
        var outcome = Assert.Single(harness.Reporter.Outcomes);
        Assert.Equal(ReconcileActionKind.StopRemove, outcome.Kind);
        Assert.Equal(ReconcileOutcomeStatus.Succeeded, outcome.Status);
    }

    [Fact]
    public async Task BlueGreen_Success_SwapsAndPromotesGreen()
    {
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v2"));
        harness.Prober.Ready = true;

        await harness.Service.RunOnceAsync();

        // Old gone, green promoted to canonical name on the new digest, no leftover green.
        Assert.False(harness.Runtime.Exists("svc-a-green"));
        Assert.True(harness.Runtime.Exists("svc-a"));
        Assert.Equal("sha256:v2", harness.Runtime.Get("svc-a")!.Digest);

        var ops = harness.Runtime.Operations.ToList();
        var startGreen = ops.IndexOf("Start:svc-a-green");
        var stopOld = ops.IndexOf("StopRemove:svc-a");
        var rename = ops.IndexOf("Rename:svc-a-green->svc-a");
        Assert.True(startGreen >= 0 && stopOld >= 0 && rename >= 0, "all blue-green steps ran");
        Assert.True(startGreen < stopOld, "green starts before old is removed");
        Assert.True(stopOld < rename, "old is removed before green is promoted");

        var outcome = Assert.Single(harness.Reporter.Outcomes);
        Assert.Equal(ReconcileActionKind.BlueGreenReplace, outcome.Kind);
        Assert.Equal(ReconcileOutcomeStatus.Succeeded, outcome.Status);
    }

    [Fact]
    public async Task BlueGreen_OldKeepsServing_UntilGreenHealthy()
    {
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v2"));

        // At the moment readiness is being checked, the old container must still
        // exist (serving) and the green candidate must already be up.
        bool oldPresentAtProbe = false, greenPresentAtProbe = false;
        harness.Prober.OnProbe = () =>
        {
            oldPresentAtProbe = harness.Runtime.Exists("svc-a");
            greenPresentAtProbe = harness.Runtime.Exists("svc-a-green");
        };
        harness.Prober.Ready = true;

        await harness.Service.RunOnceAsync();

        Assert.True(oldPresentAtProbe, "old container must still be serving while green is probed");
        Assert.True(greenPresentAtProbe, "green candidate must be running when probed");
    }

    [Fact]
    public async Task BlueGreen_Failure_AbortsCleanly_OldLeftServing()
    {
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v2"));
        harness.Prober.Ready = false; // green never becomes healthy

        await harness.Service.RunOnceAsync();

        // Old container untouched, still on the old digest; green removed; no rename.
        Assert.True(harness.Runtime.Exists("svc-a"));
        Assert.Equal("sha256:v1", harness.Runtime.Get("svc-a")!.Digest);
        Assert.False(harness.Runtime.Exists("svc-a-green"));
        Assert.DoesNotContain(harness.Runtime.Operations, op => op == "StopRemove:svc-a");
        Assert.DoesNotContain(harness.Runtime.Operations, op => op.StartsWith("Rename:"));

        var outcome = Assert.Single(harness.Reporter.Outcomes);
        Assert.Equal(ReconcileActionKind.BlueGreenReplace, outcome.Kind);
        Assert.Equal(ReconcileOutcomeStatus.Failed, outcome.Status);
    }

    [Fact]
    public async Task EnvDrift_TriggersBlueGreen()
    {
        var harness = new Harness();
        // Old container carries a stale env hash; desired env differs → replace.
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", "stale-env"));
        harness.EnvProvider.Set("svc-a", new Dictionary<string, string> { ["TOKEN"] = "new" });
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1"));
        harness.Prober.Ready = true;

        await harness.Service.RunOnceAsync();

        Assert.True(harness.Runtime.Exists("svc-a"));
        var expectedEnvHash = EnvHasher.Compute(new Dictionary<string, string> { ["TOKEN"] = "new" });
        Assert.Equal(expectedEnvHash, harness.Runtime.Get("svc-a")!.EnvHash);
        var outcome = Assert.Single(harness.Reporter.Outcomes);
        Assert.Equal(ReconcileActionKind.BlueGreenReplace, outcome.Kind);
        Assert.Equal(ReconcileOutcomeStatus.Succeeded, outcome.Status);
    }

    [Fact]
    public async Task Converged_NoActions()
    {
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1"));

        await harness.Service.RunOnceAsync();

        // No mutating container operations, no outcomes recorded for a no-op.
        Assert.Empty(harness.Runtime.Operations);
        Assert.Empty(harness.Reporter.Outcomes);
    }

    [Fact]
    public async Task Heartbeat_UpsertsInstanceStatus_WithServicesJson()
    {
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1"));

        await harness.Service.RunOnceAsync();

        var status = Assert.Single(harness.StatusStore.Upserts);
        Assert.Equal("i-test", status.InstanceId);
        Assert.Equal("10.0.0.9", status.PrivateIp);
        Assert.Equal("203.0.113.9", status.PublicIp);
        Assert.True(status.IsLeader);
        Assert.False(string.IsNullOrWhiteSpace(status.GatewayVer));
        // The per-service inventory is the documented jsonb shape.
        Assert.Contains("\"name\":\"svc-a\"", status.Services);
        Assert.Contains("\"digest\":\"sha256:v1\"", status.Services);
        Assert.Contains("\"restarts\":0", status.Services);
    }

    [Fact]
    public async Task Heartbeat_RunsEveryLoop_EvenWhenConverged()
    {
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1"));

        await harness.Service.RunOnceAsync();
        await harness.Service.RunOnceAsync();

        // A no-op convergence still heartbeats each loop.
        Assert.Equal(2, harness.StatusStore.Upserts.Count);
    }

    [Fact]
    public async Task LeaderOnly_StaleCleanup_Honored()
    {
        var harness = new Harness(isLeader: true);
        // A departed instance whose heartbeat aged out.
        harness.StatusStore.Seed(new InstanceStatus
        {
            InstanceId = "i-gone",
            HeartbeatAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
        });

        await harness.Service.RunOnceAsync();

        // Leader ran cleanup and pruned the stale row; this instance's own row remains.
        Assert.Single(harness.StatusStore.StaleCleanups);
        Assert.False(harness.StatusStore.Rows.ContainsKey("i-gone"));
        Assert.True(harness.StatusStore.Rows.ContainsKey("i-test"));
    }

    [Fact]
    public async Task NonLeader_SkipsStaleCleanup()
    {
        var harness = new Harness(isLeader: false);
        harness.StatusStore.Seed(new InstanceStatus
        {
            InstanceId = "i-gone",
            HeartbeatAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
        });

        await harness.Service.RunOnceAsync();

        // Non-leader still heartbeats (as a follower) but never prunes.
        Assert.Empty(harness.StatusStore.StaleCleanups);
        Assert.True(harness.StatusStore.Rows.ContainsKey("i-gone"));
        var status = Assert.Single(harness.StatusStore.Upserts);
        Assert.False(status.IsLeader);
    }
}
