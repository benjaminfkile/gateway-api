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
        public ServiceHostPortMap HostPorts { get; }
        public ProxyStateService ProxyState { get; }
        public ManifestProxyConfigProvider ProxyConfig { get; }

        public Harness(bool enabled = true, bool isLeader = true, MigrationReadinessGate? migrationGate = null)
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
            services.AddSingleton<IServiceAddressResolver, HostLoopbackAddressResolver>();
            services.AddSingleton<ServiceHostPortMap>();
            services.AddSingleton<ManifestProxyConfigProvider>();
            services.AddSingleton<ProxyStateService>();
            var provider = services.BuildServiceProvider();

            HostPorts = provider.GetRequiredService<ServiceHostPortMap>();
            ProxyState = provider.GetRequiredService<ProxyStateService>();
            ProxyConfig = provider.GetRequiredService<ManifestProxyConfigProvider>();

            var metadata = new InstanceMetadataProvider(
                new IInstanceMetadata[]
                {
                    new StubInstanceMetadata(new InstanceIdentity("i-test", "10.0.0.9", "203.0.113.9")),
                });

            Service = new ReconcilerService(
                Runtime,
                ProxyState,
                provider.GetRequiredService<IServiceAddressResolver>(),
                HostPorts,
                provider.GetRequiredService<IServiceScopeFactory>(),
                Prober,
                Reporter,
                metadata,
                Leader,
                Options,
                NullLogger<ReconcilerService>.Instance,
                migrationGate);
        }

        /// <summary>
        /// The YARP destination address currently configured for a service's route,
        /// or null if the service has no cluster. Reads back what
        /// <see cref="ProxyStateService"/> published to the config provider.
        /// </summary>
        public string? DestinationFor(string service)
        {
            var cluster = ProxyConfig.GetConfig().Clusters
                .FirstOrDefault(c => c.ClusterId == $"cluster-{service}");
            return cluster?.Destinations?.Values.FirstOrDefault()?.Address;
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

    private static ContainerInfo Running(string name, string digest, string? envHash, int hostPort = 8080) =>
        new(name, $"registry/{name}", digest, "running", DateTimeOffset.UnixEpoch, envHash, HostPort: hostPort);

    /// <summary>
    /// Build a standalone proxy stack (host-port map + config provider +
    /// <see cref="ProxyStateService"/>) over the given manifest store — used to
    /// simulate a fresh gateway process whose in-memory swap state is gone.
    /// </summary>
    private static (ProxyStateService State, ManifestProxyConfigProvider Config, ServiceHostPortMap HostPorts)
        BuildProxyStack(IManifestStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton<IServiceAddressResolver, HostLoopbackAddressResolver>();
        services.AddSingleton<ServiceHostPortMap>();
        services.AddSingleton<ManifestProxyConfigProvider>();
        services.AddSingleton<ProxyStateService>();
        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<ProxyStateService>(),
                sp.GetRequiredService<ManifestProxyConfigProvider>(),
                sp.GetRequiredService<ServiceHostPortMap>());
    }

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
    public async Task WaitsForMigrationGate_BeforeReconciling()
    {
        // With a DB configured, the reconciler must not converge or heartbeat until
        // migrations have been applied (tech-spec §6): gate the loop on completion.
        var gate = new MigrationReadinessGate();
        var harness = new Harness(migrationGate: gate);
        await harness.Store.UpsertAsync(Manifest("svc-a"));

        // BackgroundService.StartAsync kicks off ExecuteAsync, which parks on the gate.
        await harness.Service.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        // Gate closed → nothing has touched the runtime or published a heartbeat.
        Assert.Empty(harness.Runtime.Operations);
        Assert.False(harness.Runtime.Exists("svc-a"));
        Assert.Empty(harness.StatusStore.Upserts);

        // Open the gate → the reconciler proceeds and converges the box.
        gate.MarkReady();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!harness.Runtime.Exists("svc-a") && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(harness.Runtime.Exists("svc-a"));

        await harness.Service.StopAsync(CancellationToken.None);
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
    public async Task BlueGreen_Promote_Destination_TargetsActualSidePort()
    {
        // The core bug: Docker port bindings are fixed at create time, so a
        // promoted green candidate keeps its side port. Traffic must follow it.
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash, hostPort: 8080));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v2", port: 8080));
        harness.Prober.Ready = true;

        await harness.Service.RunOnceAsync();

        var expectedPort = 8080 + harness.Options.SidePortOffset; // 8081

        // The container really is bound to the side port after promotion.
        Assert.Equal(expectedPort, harness.Runtime.Get("svc-a")!.HostPort);
        // The container-truth map records it, and the YARP route targets it — no
        // stale override, and NOT the manifest port where nothing listens.
        Assert.True(harness.HostPorts.TryGet("svc-a", out var mapped));
        Assert.Equal(expectedPort, mapped);
        Assert.Equal($"http://127.0.0.1:{expectedPort}", harness.DestinationFor("svc-a"));
    }

    [Fact]
    public async Task AfterRestart_RoutesBuiltFromInventory_TargetPromotedSidePort()
    {
        // Simulate a gateway restart after a successful deploy: a promoted-green
        // container is bound to the side port, running the current digest, but the
        // in-memory swap state that recorded that port is gone. On startup the route
        // table must be rebuilt from container truth (ProxyRouteInitializer), so the
        // service keeps receiving traffic on its real port.
        var runtime = new FakeContainerRuntime();
        runtime.Seed(new ContainerInfo(
            "svc-a", "registry/svc-a", "sha256:v2", "running",
            DateTimeOffset.UnixEpoch, EmptyEnvHash, HostPort: 8081));

        var store = new InMemoryManifestStore();
        await store.UpsertAsync(Manifest("svc-a", digest: "sha256:v2", port: 8080));

        var (state, config, hostPorts) = BuildProxyStack(store);
        var initializer = new ProxyRouteInitializer(
            state, hostPorts, NullLogger<ProxyRouteInitializer>.Instance, runtime);

        await initializer.StartAsync(CancellationToken.None);

        Assert.True(hostPorts.TryGet("svc-a", out var mapped));
        Assert.Equal(8081, mapped);
        var cluster = Assert.Single(config.GetConfig().Clusters);
        Assert.Equal("http://127.0.0.1:8081", cluster.Destinations!.Values.Single().Address);
    }

    [Fact]
    public async Task Deploy_Promote_ThenNextLoop_IsNoOp()
    {
        // Regression for the observed replace churn (tech-spec §7): a container
        // serving on the side port with the correct digest/env must NOT be flagged
        // as drift. Deploy -> promote -> next reconcile loop is a pure no-op.
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash, hostPort: 8080));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v2", port: 8080));
        harness.Prober.Ready = true;

        await harness.Service.RunOnceAsync(); // deploy + promote
        Assert.Equal("sha256:v2", harness.Runtime.Get("svc-a")!.Digest);

        harness.Reporter.Outcomes.Clear();
        var opsBefore = harness.Runtime.Operations.Count;

        await harness.Service.RunOnceAsync(); // steady state

        var newOps = harness.Runtime.Operations.Skip(opsBefore).ToList();
        Assert.Empty(newOps);
        Assert.Empty(harness.Reporter.Outcomes);
    }

    [Fact]
    public async Task BlueGreen_Failure_LeavesRouteOnOldContainerPort()
    {
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash, hostPort: 8080));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v2", port: 8080));
        harness.Prober.Ready = false; // green never becomes healthy

        // Routes as they would be after startup — proven not to move on abort.
        harness.HostPorts.ReplaceFrom(await harness.Runtime.ListManagedContainersAsync());
        await harness.ProxyState.RefreshRoutesAsync();
        Assert.Equal("http://127.0.0.1:8080", harness.DestinationFor("svc-a"));

        await harness.Service.RunOnceAsync();

        // Old container untouched on its real port; route and map unchanged.
        Assert.True(harness.Runtime.Exists("svc-a"));
        Assert.Equal(8080, harness.Runtime.Get("svc-a")!.HostPort);
        Assert.True(harness.HostPorts.TryGet("svc-a", out var mapped));
        Assert.Equal(8080, mapped);
        Assert.Equal("http://127.0.0.1:8080", harness.DestinationFor("svc-a"));
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
