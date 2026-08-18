using System.Collections.Concurrent;
using Gateway.Api.Containers;
using Gateway.Api.Data;
using Gateway.Api.Instances;
using Gateway.Api.Management;
using Gateway.Api.Manifest;
using Gateway.Api.Proxy;
using Gateway.Api.Reconcile;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    /// <summary>
    /// Captures every ILogger call so tests can assert transition logging
    /// (task #98: log Warning on enter/leave no-container state, once per transition).
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Category, string Message)> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries, categoryName);
        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly ConcurrentQueue<(LogLevel, string, string)> _entries;
            private readonly string _category;

            public CapturingLogger(ConcurrentQueue<(LogLevel, string, string)> entries, string category)
            {
                _entries = entries;
                _category = category;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                _entries.Enqueue((logLevel, _category, formatter(state, exception)));
        }
    }

    /// <summary>Assembles a reconciler wired to fakes plus a real ProxyStateService.</summary>
    private sealed class Harness
    {
        public CapturingLoggerProvider Logs { get; } = new();

        public InMemoryManifestStore Store { get; } = new();
        public FakeContainerRuntime Runtime { get; } = new();
        public FakeReadinessProber Prober { get; } = new();
        public FakeReporter Reporter { get; } = new();
        public FakeEnvProvider EnvProvider { get; } = new();
        public FakeInstanceStatusStore StatusStore { get; } = new();
        public FakeLogGroupAdmin LogGroupAdmin { get; } = new();
        public InMemoryLeaderElection Leader { get; }
        public ReconcilerOptions Options { get; }
        public ReconcilerService Service { get; }
        public ServiceHostPortMap HostPorts { get; }
        public ProxyStateService ProxyState { get; }
        public ManifestProxyConfigProvider ProxyConfig { get; }
        // A hand-cranked clock that drives ProxyStateService's swap-age math (task #99).
        // Tests can advance it to expire the bounded mid-swap skip window without
        // waiting real seconds. Defaults to the Unix epoch so existing tests that
        // never touch it see a stable, non-drifting now().
        public ManualTimeProvider Clock { get; } = new() { Now = DateTimeOffset.UnixEpoch };

        public Harness(
            bool enabled = true,
            bool isLeader = true,
            MigrationReadinessGate? migrationGate = null,
            IServiceEnvProvider? envProvider = null)
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
            // Default to the in-test FakeEnvProvider; a caller may inject the real
            // SecretsManagerEnvProvider (over a fake secret store) to exercise the
            // secrets → env-drift path end to end.
            services.AddSingleton<IServiceEnvProvider>(envProvider ?? EnvProvider);
            services.AddSingleton<IInstanceStatusStore>(StatusStore);
            services.AddSingleton<ILogGroupAdmin>(LogGroupAdmin);
            services.AddSingleton<IServiceAddressResolver, HostLoopbackAddressResolver>();
            services.AddSingleton<ServiceHostPortMap>();
            services.AddSingleton<ManifestProxyConfigProvider>();
            services.AddSingleton<ProxyStateService>();
            // Register the SAME ReconcilerOptions the ReconcilerService uses so
            // ProxyStateService sees the reconciler-enabled flag (task #98:
            // suppresses the manifest-port fallback in reconciler mode).
            services.AddSingleton(Options);
            // Drive ProxyStateService's swap-age math off a controllable clock so
            // tests can expire the bounded mid-swap skip window (task #99).
            services.AddSingleton<TimeProvider>(Clock);
            services.AddLogging(logging => logging.AddProvider(Logs));
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
                // Use the captured logger (not NullLogger) so tests can assert
                // transition logs — e.g. the once-per-trip Warning from the
                // digest-drift circuit breaker (task #99).
                provider.GetRequiredService<ILogger<ReconcilerService>>(),
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

        /// <summary>
        /// Whether a service has a cluster whose destination set is empty — the
        /// reconciler-mode 'no canonical container port' signal that makes YARP
        /// serve 503 rather than silently proxying to a stranger.
        /// </summary>
        public bool ClusterHasNoDestinations(string service)
        {
            var cluster = ProxyConfig.GetConfig().Clusters
                .FirstOrDefault(c => c.ClusterId == $"cluster-{service}");
            return cluster is not null && (cluster.Destinations is null || cluster.Destinations.Count == 0);
        }
    }

    private static ServiceManifest Manifest(
        string name,
        string status = "running",
        string? digest = "sha256:v1",
        int port = 8080,
        DateTimeOffset? restartRequestedAt = null,
        string? envSecretRef = null,
        string? realtimePublishToken = null) => new()
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
        RestartRequestedAt = restartRequestedAt,
        EnvSecretRef = envSecretRef,
        RealtimePublishToken = realtimePublishToken,
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
    public async Task Start_InjectsRealtimeToken_IntoContainerEnv()
    {
        // task #593: the reconciler injects GATEWAY_REALTIME_TOKEN into every managed
        // container's env so the service can authorize POST /internal/publish for its
        // own channels. A service with no token yet gets no such variable.
        var harness = new Harness();
        await harness.Store.UpsertAsync(Manifest("svc-a", realtimePublishToken: "secret-a"));

        await harness.Service.RunOnceAsync();

        var spec = Assert.Single(harness.Runtime.StartedSpecs);
        Assert.Equal("secret-a", spec.EnvVars["GATEWAY_REALTIME_TOKEN"]);
    }

    [Fact]
    public async Task Start_NoRealtimeToken_OmitsEnvVar()
    {
        var harness = new Harness();
        await harness.Store.UpsertAsync(Manifest("svc-a", realtimePublishToken: null));

        await harness.Service.RunOnceAsync();

        var spec = Assert.Single(harness.Runtime.StartedSpecs);
        Assert.DoesNotContain("GATEWAY_REALTIME_TOKEN", spec.EnvVars.Keys);
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
    public async Task Start_StalePinnedDigest_FallsBackToPulledDigest()
    {
        // Defense in depth (tech-spec §7): the manifest pins a digest that no longer
        // exists locally (the production 2026-08-08 stale-digest loop). After the pull
        // of image:tag succeeds, the start by the stale digest fails image-not-found,
        // so the reconciler must retry with the freshly pulled digest and succeed.
        var harness = new Harness();
        harness.Runtime.PullDigest = (image, tag) => "sha256:fresh";
        harness.Runtime.MissingDigests.Add("sha256:stale");
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:stale"));

        await harness.Service.RunOnceAsync();

        Assert.True(harness.Runtime.Exists("svc-a"));
        // The container came up on the pulled digest, not the stale pinned one.
        Assert.Equal("sha256:fresh", harness.Runtime.Get("svc-a")!.Digest);
        // The first start attempt (stale digest) was observed to fail, then a retry ran.
        Assert.Contains(harness.Runtime.Operations, op => op == "StartFailedImageNotFound:svc-a");
        var outcome = Assert.Single(harness.Reporter.Outcomes);
        Assert.Equal(ReconcileActionKind.Start, outcome.Kind);
        Assert.Equal(ReconcileOutcomeStatus.Succeeded, outcome.Status);
    }

    [Fact]
    public async Task BlueGreen_StalePinnedDigest_FallsBackToPulledDigest()
    {
        // Same stale-pin defense, on the blue-green path: the green candidate's start
        // by the pinned digest fails image-not-found after a successful pull, so it
        // retries on the pulled digest, becomes healthy, and is promoted.
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash));
        harness.Runtime.PullDigest = (image, tag) => "sha256:fresh";
        harness.Runtime.MissingDigests.Add("sha256:stale");
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:stale"));
        harness.Prober.Ready = true;

        await harness.Service.RunOnceAsync();

        Assert.True(harness.Runtime.Exists("svc-a"));
        Assert.False(harness.Runtime.Exists("svc-a-green"));
        Assert.Equal("sha256:fresh", harness.Runtime.Get("svc-a")!.Digest);
        Assert.Contains(harness.Runtime.Operations, op => op == "StartFailedImageNotFound:svc-a-green");
        var outcome = Assert.Single(harness.Reporter.Outcomes);
        Assert.Equal(ReconcileActionKind.BlueGreenReplace, outcome.Kind);
        Assert.Equal(ReconcileOutcomeStatus.Succeeded, outcome.Status);
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
    public async Task BlueGreen_ConsecutiveDeploys_EachGetFreshEphemeralPort()
    {
        // Host ports are Docker-assigned now: there is NO alternation between a
        // manifest port and a side port. Each deploy's green candidate gets a fresh
        // ephemeral host port and the promoted container adopts it.
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash));
        harness.Prober.Ready = true;

        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v2"));
        await harness.Service.RunOnceAsync();
        var portAfterFirst = harness.Runtime.Get("svc-a")!.HostPort;
        Assert.NotNull(portAfterFirst);
        Assert.NotEqual(8080, portAfterFirst); // never the container-internal port

        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v3"));
        await harness.Service.RunOnceAsync();
        var portAfterSecond = harness.Runtime.Get("svc-a")!.HostPort;
        Assert.Equal("sha256:v3", harness.Runtime.Get("svc-a")!.Digest);
        // A fresh Docker-assigned port each time — not the previous port re-used and
        // not an alternation back to the manifest port.
        Assert.NotEqual(portAfterFirst, portAfterSecond);
        Assert.NotEqual(8080, portAfterSecond);
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
    public async Task BlueGreen_Promote_Destination_TargetsAssignedHostPort()
    {
        // Docker port bindings are fixed at create time, so a promoted green keeps
        // the ephemeral host port Docker assigned it. Traffic must follow it, not
        // the manifest (container-internal) port where nothing is host-published.
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash, hostPort: 8080));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v2", port: 8080));
        harness.Prober.Ready = true;

        await harness.Service.RunOnceAsync();

        // The container is bound to the Docker-assigned ephemeral port after promotion.
        var assigned = harness.Runtime.Get("svc-a")!.HostPort;
        Assert.NotNull(assigned);
        Assert.NotEqual(8080, assigned);
        // The container-truth map records it, and the YARP route targets it — no
        // stale override, and NOT the manifest port where nothing listens.
        Assert.True(harness.HostPorts.TryGet("svc-a", out var mapped));
        Assert.Equal(assigned, mapped);
        Assert.Equal($"http://127.0.0.1:{assigned}", harness.DestinationFor("svc-a"));
    }

    [Fact]
    public async Task Start_RecordsAssignedHostPort_AndRoutesToIt()
    {
        // A fresh start publishes the container-internal port with an unassigned host
        // binding; Docker's assigned host port is fed into the map and routed to.
        var harness = new Harness();
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1", port: 8080));

        await harness.Service.RunOnceAsync();

        var assigned = harness.Runtime.Get("svc-a")!.HostPort;
        Assert.NotNull(assigned);
        Assert.NotEqual(8080, assigned); // dynamic host port, not the container-internal port
        Assert.True(harness.HostPorts.TryGet("svc-a", out var mapped));
        Assert.Equal(assigned, mapped);
        Assert.Equal($"http://127.0.0.1:{assigned}", harness.DestinationFor("svc-a"));
    }

    [Fact]
    public async Task BlueGreen_ProbesGreen_AtAssignedHostPort()
    {
        // Readiness must probe the green candidate at the host port Docker assigned
        // it, never the manifest (container-internal) port.
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash, hostPort: 8080));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v2", port: 8080));
        harness.Prober.Ready = true;

        await harness.Service.RunOnceAsync();

        // The promoted container keeps the green candidate's assigned port.
        var greenPort = harness.Runtime.Get("svc-a")!.HostPort;
        Assert.NotNull(greenPort);
        Assert.Contains($"http://127.0.0.1:{greenPort}", harness.Prober.ProbedAddresses);
        Assert.DoesNotContain("http://127.0.0.1:8080", harness.Prober.ProbedAddresses);
    }

    [Fact]
    public async Task BlueGreen_ReplacesPreExistingFixedPortContainer_WithEphemeralPort()
    {
        // Migration/coexistence (tech-spec §7): a container from an older gateway is
        // bound to a FIXED host port (== the manifest port). No migration step is
        // required — its next replace starts a green on a Docker-assigned ephemeral
        // port (the two coexist; Docker guarantees uniqueness) and the promoted green
        // adopts the ephemeral port, with traffic following it.
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash, hostPort: 8080));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v2", port: 8080));
        harness.Prober.Ready = true;

        // Route as primed at startup from the pre-existing fixed-port container.
        harness.HostPorts.ReplaceFrom(await harness.Runtime.ListManagedContainersAsync());
        await harness.ProxyState.RefreshRoutesAsync();
        Assert.Equal("http://127.0.0.1:8080", harness.DestinationFor("svc-a"));

        await harness.Service.RunOnceAsync();

        Assert.Equal("sha256:v2", harness.Runtime.Get("svc-a")!.Digest);
        var promoted = harness.Runtime.Get("svc-a")!.HostPort;
        Assert.NotNull(promoted);
        Assert.NotEqual(8080, promoted); // adopted an ephemeral port on replace
        Assert.True(harness.HostPorts.TryGet("svc-a", out var mapped));
        Assert.Equal(promoted, mapped);
        Assert.Equal($"http://127.0.0.1:{promoted}", harness.DestinationFor("svc-a"));
    }

    [Fact]
    public async Task Reconcile_ContainerStabilizesOnNewPort_NoStartAction_MapAndRouteUpdatedNextLoop()
    {
        // Production 2026-08-08: a container recovered (Docker restart policy / crash-loop
        // stabilization) on a host port the map did not hold, with NO reconciler start or
        // promote action. The in-memory map + YARP route must catch up within one loop,
        // no gateway restart (requirements #1, #4). Here the container is converged
        // (digest/env match the manifest) so the plan produces no action for it.
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash, hostPort: 32777));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1", port: 8080));

        await harness.Service.RunOnceAsync();

        // No container mutation happened — the map/route caught up purely from truth.
        Assert.DoesNotContain(harness.Runtime.Operations, op => op.StartsWith("Start:"));
        Assert.DoesNotContain(harness.Runtime.Operations, op => op.StartsWith("Rename:"));
        Assert.True(harness.HostPorts.TryGet("svc-a", out var mapped));
        Assert.Equal(32777, mapped);
        // The route resolves to the container's real port, not the manifest fallback.
        Assert.Equal("http://127.0.0.1:32777", harness.DestinationFor("svc-a"));
    }

    [Fact]
    public async Task Reconcile_PortChangesBetweenLoops_RouteFollowsWithoutAction()
    {
        // A managed container's published port changes outside any reconciler action
        // (e.g. manual docker intervention recreated it). The next loop must re-point
        // the route at the new real port with no start/promote.
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash, hostPort: 40000));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1", port: 8080));

        await harness.Service.RunOnceAsync();
        Assert.Equal("http://127.0.0.1:40000", harness.DestinationFor("svc-a"));

        // Container reappears on a different host port with no reconciler action.
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash, hostPort: 40001));
        var opsBefore = harness.Runtime.Operations.Count;

        await harness.Service.RunOnceAsync();

        Assert.Empty(harness.Runtime.Operations.Skip(opsBefore)); // no container mutation
        Assert.True(harness.HostPorts.TryGet("svc-a", out var mapped));
        Assert.Equal(40001, mapped);
        Assert.Equal("http://127.0.0.1:40001", harness.DestinationFor("svc-a"));
    }

    [Fact]
    public async Task Reconcile_SteadyPort_DoesNotRebuildRoutes()
    {
        // Requirement #3: no unnecessary route churn. A converged fleet whose ports are
        // unchanged must not rebuild the YARP config (the provider hands back the SAME
        // config instance when nothing was published).
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash, hostPort: 40000));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1", port: 8080));

        await harness.Service.RunOnceAsync(); // primes map + route to 40000
        var configAfterFirst = harness.ProxyConfig.GetConfig();

        await harness.Service.RunOnceAsync(); // steady state, port unchanged

        // Same config object => no route rebuild this loop.
        Assert.Same(configAfterFirst, harness.ProxyConfig.GetConfig());
        Assert.Equal("http://127.0.0.1:40000", harness.DestinationFor("svc-a"));
    }

    [Fact]
    public async Task Reconcile_MultipleServices_OnlyChangedOneTriggersRefresh()
    {
        // Requirement #5: with several services, only the one whose real port moved
        // triggers a refresh; the unchanged service's destination is preserved.
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash, hostPort: 40000));
        harness.Runtime.Seed(Running("svc-b", "sha256:v1", EmptyEnvHash, hostPort: 40001));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1", port: 8080));
        await harness.Store.UpsertAsync(Manifest("svc-b", digest: "sha256:v1", port: 8080));

        await harness.Service.RunOnceAsync(); // prime both routes
        var configAfterFirst = harness.ProxyConfig.GetConfig();

        // Only svc-b moves.
        harness.Runtime.Seed(Running("svc-b", "sha256:v1", EmptyEnvHash, hostPort: 40002));

        await harness.Service.RunOnceAsync();

        Assert.NotSame(configAfterFirst, harness.ProxyConfig.GetConfig()); // a refresh ran
        Assert.Equal("http://127.0.0.1:40000", harness.DestinationFor("svc-a")); // unchanged
        Assert.Equal("http://127.0.0.1:40002", harness.DestinationFor("svc-b")); // followed truth
    }

    [Fact]
    public async Task Reconcile_MidSwapOverride_NotClobbered()
    {
        // Requirement #2: while a service's blue-green destination override is active,
        // the per-loop container-truth reconcile must not touch its canonical port nor
        // move its route off the override. The service is converged (digest/env match)
        // so the plan takes no action for it.
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash, hostPort: 40000));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1", port: 8080));

        await harness.Service.RunOnceAsync(); // map + route primed to 40000
        Assert.Equal("http://127.0.0.1:40000", harness.DestinationFor("svc-a"));

        // Simulate an in-flight swap: the route is overridden onto a green candidate.
        await harness.ProxyState.SwapDestinationAsync("svc-a", "http://127.0.0.1:59999");
        Assert.Equal("http://127.0.0.1:59999", harness.DestinationFor("svc-a"));

        // Container truth for the canonical container appears to move; the loop must
        // NOT clobber the canonical map entry or the active override.
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash, hostPort: 41000));

        await harness.Service.RunOnceAsync();

        // Canonical map still holds the pre-swap port (skipped), override still routes.
        Assert.True(harness.HostPorts.TryGet("svc-a", out var mapped));
        Assert.Equal(40000, mapped);
        Assert.Equal("http://127.0.0.1:59999", harness.DestinationFor("svc-a"));
    }

    [Fact]
    public async Task StopRemove_ClearsHostPortMap()
    {
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash, hostPort: 8080));
        harness.HostPorts.Set("svc-a", 8080);
        await harness.Store.UpsertAsync(Manifest("svc-a", status: "stopped"));

        await harness.Service.RunOnceAsync();

        // Container gone and its host-port mapping forgotten.
        Assert.False(harness.Runtime.Exists("svc-a"));
        Assert.False(harness.HostPorts.TryGet("svc-a", out _));
    }

    [Fact]
    public async Task OrphanContainer_NoManifestEntry_StopRemove()
    {
        // The delete → orphan-cleanup handshake (tech-spec §4.3): after
        // DELETE /mgmt/services/{name} removes the row, the still-running managed
        // container has no manifest entry, so the next reconcile loop treats it as
        // an orphan and stops/removes it — no separate teardown call needed.
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash, hostPort: 8080));
        harness.HostPorts.Set("svc-a", 8080);
        // Manifest store is empty (the row was deleted).

        await harness.Service.RunOnceAsync();

        Assert.False(harness.Runtime.Exists("svc-a"));
        Assert.False(harness.HostPorts.TryGet("svc-a", out _));
        var outcome = Assert.Single(harness.Reporter.Outcomes);
        Assert.Equal(ReconcileActionKind.StopRemove, outcome.Kind);
        Assert.Equal(ReconcileOutcomeStatus.Succeeded, outcome.Status);
    }

    [Fact]
    public async Task AfterRestart_RoutesBuiltFromInventory_TargetAssignedHostPort()
    {
        // Simulate a gateway restart after a successful deploy: the container is
        // bound to its Docker-assigned host port, running the current digest, but the
        // in-memory state that recorded that port is gone. On startup the route table
        // must be rebuilt from container truth (ProxyRouteInitializer), so the service
        // keeps receiving traffic on its real (assigned) port.
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
        // serving on its assigned host port with the correct digest/env must NOT be flagged
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
    public async Task Restart_RecreatesStaleContainerOnce_ThenImmune()
    {
        // A restart stamps restart_requested_at (tech-spec §4.5). The already-running
        // container started before the stamp, so it is stale and gets a blue-green
        // recreate on the SAME digest/env. The recreated container is stamped after the
        // request, so the next loop is a pure no-op — no restart loop (requirement #3).
        var harness = new Harness();
        var restartAt = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
        // The old container started at the epoch, well before the restart request.
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash, hostPort: 8080));
        // Containers the fake (re)creates are stamped after the request → satisfy it.
        harness.Runtime.NewContainerStartedAt = restartAt + TimeSpan.FromSeconds(30);
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1", restartRequestedAt: restartAt));
        harness.Prober.Ready = true;

        await harness.Service.RunOnceAsync();

        // Same digest/env, but a full blue-green recreate ran (old removed, green promoted).
        Assert.True(harness.Runtime.Exists("svc-a"));
        Assert.Equal("sha256:v1", harness.Runtime.Get("svc-a")!.Digest);
        Assert.Contains(harness.Runtime.Operations, op => op == "Rename:svc-a-green->svc-a");
        var outcome = Assert.Single(harness.Reporter.Outcomes);
        Assert.Equal(ReconcileActionKind.BlueGreenReplace, outcome.Kind);
        Assert.Equal(ReconcileOutcomeStatus.Succeeded, outcome.Status);
        Assert.Contains("restart requested", outcome.Detail);

        // Second loop: the recreated container satisfies the request → nothing happens.
        harness.Reporter.Outcomes.Clear();
        var opsBefore = harness.Runtime.Operations.Count;

        await harness.Service.RunOnceAsync();

        Assert.Empty(harness.Runtime.Operations.Skip(opsBefore));
        Assert.Empty(harness.Reporter.Outcomes);
    }

    [Fact]
    public async Task Restart_OldKeepsServing_UntilGreenHealthy()
    {
        // Blue-green invariant on the restart path (requirement #5): the old container
        // keeps serving until the green candidate is healthy.
        var harness = new Harness();
        var restartAt = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash, hostPort: 8080));
        harness.Runtime.NewContainerStartedAt = restartAt + TimeSpan.FromSeconds(30);
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1", restartRequestedAt: restartAt));

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
    public async Task Restart_StoppedService_BehavesLikeStart()
    {
        // Requirement #4: a restart request on a service whose desired container is
        // absent just starts it (the endpoint also asserts desired=running). Here the
        // stale stamp is present but there is no container, so the plan is a plain Start.
        var harness = new Harness();
        var restartAt = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
        harness.Runtime.NewContainerStartedAt = restartAt + TimeSpan.FromSeconds(30);
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1", restartRequestedAt: restartAt));

        await harness.Service.RunOnceAsync();

        Assert.True(harness.Runtime.Exists("svc-a"));
        var outcome = Assert.Single(harness.Reporter.Outcomes);
        Assert.Equal(ReconcileActionKind.Start, outcome.Kind);
        Assert.Equal(ReconcileOutcomeStatus.Succeeded, outcome.Status);
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
    public async Task SecretsEnv_ChangedResolvedValue_TriggersBlueGreenReplace()
    {
        // Requirement #5 (reconciler-level): the real SecretsManagerEnvProvider (over a
        // fake secret store) resolves a service's env at reconcile time; when the secret
        // value later changes, the resolved env hash drifts from the running container's
        // and the reconciler blue-green-replaces it — rotation propagates via env drift.
        var store = new FakeSecretStore();
        store.Secrets["svc-a-secret"] = "{\"TOKEN\":\"v1\"}";
        var time = new ManualTimeProvider();
        var provider = new SecretsManagerEnvProvider(store, TimeSpan.FromSeconds(60), time);

        var harness = new Harness(envProvider: provider);
        var v1Hash = EnvHasher.Compute(new Dictionary<string, string> { ["TOKEN"] = "v1" });
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", v1Hash, hostPort: 8080));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1", port: 8080, envSecretRef: "svc-a-secret"));
        harness.Prober.Ready = true;

        // First loop: resolved env matches the running container → nothing to do.
        await harness.Service.RunOnceAsync();
        Assert.Empty(harness.Reporter.Outcomes);
        Assert.Equal(1, store.Calls);

        // Rotate the secret value and let the cache TTL lapse.
        store.Secrets["svc-a-secret"] = "{\"TOKEN\":\"v2\"}";
        time.Now += TimeSpan.FromSeconds(61);

        // Second loop: the resolved env hash drifts → blue-green replace onto the new env.
        await harness.Service.RunOnceAsync();

        var v2Hash = EnvHasher.Compute(new Dictionary<string, string> { ["TOKEN"] = "v2" });
        Assert.Equal(v2Hash, harness.Runtime.Get("svc-a")!.EnvHash);
        var outcome = Assert.Single(harness.Reporter.Outcomes);
        Assert.Equal(ReconcileActionKind.BlueGreenReplace, outcome.Kind);
        Assert.Equal(ReconcileOutcomeStatus.Succeeded, outcome.Status);
        Assert.Equal(2, store.Calls); // refetched after TTL, not per loop
    }

    [Fact]
    public async Task SecretsEnv_ResolutionFailure_IsolatedToOneService()
    {
        // Requirement #4: a service whose secret cannot be resolved (missing secret)
        // fails only itself — the other service still converges, the loop never throws,
        // and the failing service surfaces a ref-only error (no secret value) through the
        // heartbeat lastError plumbing.
        var store = new FakeSecretStore();
        store.Secrets["svc-a-secret"] = "{\"TOKEN\":\"ok\"}";
        // svc-b-secret is intentionally absent → the store throws for it.
        var provider = new SecretsManagerEnvProvider(store, TimeSpan.FromSeconds(60));

        var harness = new Harness(envProvider: provider);
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1", envSecretRef: "svc-a-secret"));
        await harness.Store.UpsertAsync(Manifest("svc-b", digest: "sha256:v1", envSecretRef: "svc-b-secret"));

        await harness.Service.RunOnceAsync();

        // svc-a converged normally.
        Assert.True(harness.Runtime.Exists("svc-a"));
        Assert.Equal(
            EnvHasher.Compute(new Dictionary<string, string> { ["TOKEN"] = "ok" }),
            harness.Runtime.Get("svc-a")!.EnvHash);

        // svc-b never started (its env could not be resolved), but the loop survived.
        Assert.False(harness.Runtime.Exists("svc-b"));

        // The failure is surfaced as a service-scoped outcome and heartbeat entry...
        var failed = Assert.Single(harness.Reporter.Outcomes, o => o.ServiceName == "svc-b");
        Assert.Equal(ReconcileOutcomeStatus.Failed, failed.Status);
        Assert.Contains("svc-b-secret", failed.Detail); // names the ref
        Assert.DoesNotContain("ok", failed.Detail);       // never any secret value

        var entry = HeartbeatEntry(harness, "svc-b");
        Assert.NotNull(entry);
        Assert.Equal(InstanceServicesJson.AbsentState, entry!.State);
        Assert.False(string.IsNullOrEmpty(entry.LastError));
        Assert.NotNull(entry.LastErrorAt);

        // svc-a is clean (no error entry).
        var okEntry = HeartbeatEntry(harness, "svc-a");
        Assert.NotNull(okEntry);
        Assert.Null(okEntry!.LastError);
    }

    [Fact]
    public async Task SecretsEnv_TransientFailure_ClearsAfterRecovery()
    {
        // A service whose secret is temporarily unresolvable records an error; once the
        // secret resolves on a later loop, the error clears and the service converges.
        var store = new FakeSecretStore();
        var provider = new SecretsManagerEnvProvider(store, TimeSpan.FromSeconds(60));

        var harness = new Harness(envProvider: provider);
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1", envSecretRef: "svc-a-secret"));

        // First loop: secret missing → error recorded, no container.
        await harness.Service.RunOnceAsync();
        Assert.False(harness.Runtime.Exists("svc-a"));
        Assert.False(string.IsNullOrEmpty(HeartbeatEntry(harness, "svc-a")!.LastError));

        // Fix the secret and reconcile again: the service starts and the error clears.
        store.Secrets["svc-a-secret"] = "{\"TOKEN\":\"ok\"}";
        await harness.Service.RunOnceAsync();

        Assert.True(harness.Runtime.Exists("svc-a"));
        Assert.Null(HeartbeatEntry(harness, "svc-a")!.LastError);
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

    [Fact]
    public async Task Start_AppliesPerServiceAwsLogsConfig()
    {
        // tech-spec §4.3: every container ships to CloudWatch group
        // /gateway/services/{service} with a stream per instance (this instance's id).
        var harness = new Harness();
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1"));

        await harness.Service.RunOnceAsync();

        var spec = Assert.Single(harness.Runtime.StartedSpecs);
        Assert.Equal("awslogs", spec.LogConfig.Driver);
        Assert.Equal("/gateway/services/svc-a", spec.LogConfig.Options["awslogs-group"]);
        Assert.Equal("i-test", spec.LogConfig.Options["awslogs-stream"]);
        Assert.Equal("true", spec.LogConfig.Options["awslogs-create-group"]);
        // The write-path group must equal the read-path template exactly (requirement #4).
        Assert.Equal(CloudWatchLogStore.LogGroupFor("svc-a"), spec.LogConfig.Options["awslogs-group"]);
    }

    [Fact]
    public async Task BlueGreen_GreenInheritsServiceGroupAndStream()
    {
        // The green candidate is the {name}-green container, but its logs must land in
        // the canonical service group/stream so they survive promotion (tech-spec §4.3).
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash, hostPort: 8080));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v2", port: 8080));
        harness.Prober.Ready = true;

        await harness.Service.RunOnceAsync();

        var greenSpec = harness.Runtime.StartedSpecs.Single(s => s.Name == "svc-a-green");
        Assert.Equal("awslogs", greenSpec.LogConfig.Driver);
        // Group/stream are the service's, NOT the -green container name.
        Assert.Equal("/gateway/services/svc-a", greenSpec.LogConfig.Options["awslogs-group"]);
        Assert.Equal("i-test", greenSpec.LogConfig.Options["awslogs-stream"]);
    }

    [Fact]
    public async Task EscapeHatch_JsonFileDriver_UsesLocalRotation()
    {
        // Escape hatch (tech-spec §4.3): a dev box without AWS forces json-file with
        // sane rotation. No awslogs group/stream is set.
        var harness = new Harness();
        harness.Options.LogDriver.Driver = "json-file";
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1"));

        await harness.Service.RunOnceAsync();

        var spec = Assert.Single(harness.Runtime.StartedSpecs);
        Assert.Equal("json-file", spec.LogConfig.Driver);
        Assert.Equal("10m", spec.LogConfig.Options["max-size"]);
        Assert.Equal("3", spec.LogConfig.Options["max-file"]);
        Assert.False(spec.LogConfig.Options.ContainsKey("awslogs-group"));
        // No CloudWatch group exists under the escape hatch, so no retention call.
        Assert.Empty(harness.LogGroupAdmin.Calls);
    }

    [Fact]
    public async Task Retention_SetOncePerServiceGroup_AcrossLoops()
    {
        // tech-spec §9: 30-day retention set once per service group (awslogs-create-group
        // cannot set it). Two services → two groups; repeated loops do not re-issue.
        var harness = new Harness();
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1"));
        await harness.Store.UpsertAsync(Manifest("svc-b", digest: "sha256:v1"));

        await harness.Service.RunOnceAsync();
        await harness.Service.RunOnceAsync();

        Assert.Equal(2, harness.LogGroupAdmin.Calls.Count);
        Assert.Contains(("/gateway/services/svc-a", 30), harness.LogGroupAdmin.Calls);
        Assert.Contains(("/gateway/services/svc-b", 30), harness.LogGroupAdmin.Calls);
    }

    [Fact]
    public async Task Retention_NotLeaderGated_FollowerStillSetsIt()
    {
        // Retention is not a fleet-wide/leader-only duty (requirement #3): a follower
        // that starts a container still ensures its group's retention.
        var harness = new Harness(isLeader: false);
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1"));

        await harness.Service.RunOnceAsync();

        Assert.Contains(("/gateway/services/svc-a", 30), harness.LogGroupAdmin.Calls);
    }

    /// <summary>The svc entry in the most recent heartbeat's services JSON, or null when absent.</summary>
    private static InstanceServiceEntry? HeartbeatEntry(Harness harness, string service) =>
        InstanceServicesJson.Parse(harness.StatusStore.Upserts[^1].Services)
            .FirstOrDefault(e => e.Name == service);

    [Fact]
    public async Task Heartbeat_RecordsLastError_OnFailure_ThenClearsOnSuccess()
    {
        // Requirement #1/#4: a failed reconcile action records lastError/lastErrorAt on
        // the service's heartbeat entry; a subsequent success for the same service
        // clears them. Uses the blue-green abort path (old container keeps serving).
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash, hostPort: 8080));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v2", port: 8080));
        harness.Prober.Ready = false; // green never becomes healthy -> failure

        await harness.Service.RunOnceAsync();

        var failed = HeartbeatEntry(harness, "svc-a");
        Assert.NotNull(failed);
        Assert.Equal("running", failed!.State); // old container still serving
        Assert.False(string.IsNullOrEmpty(failed.LastError));
        Assert.NotNull(failed.LastErrorAt);

        // Now the green candidate becomes healthy: the replace succeeds and clears the error.
        harness.Prober.Ready = true;
        await harness.Service.RunOnceAsync();

        var recovered = HeartbeatEntry(harness, "svc-a");
        Assert.NotNull(recovered);
        Assert.Equal("sha256:v2", recovered!.Digest);
        Assert.Null(recovered.LastError);
        Assert.Null(recovered.LastErrorAt);
    }

    [Fact]
    public async Task Heartbeat_AbsentButDesired_FailedStart_CarriesErrorEntry()
    {
        // Requirement #3: a service desired running whose start keeps failing has NO
        // container, but must still get an entry carrying the error — otherwise the
        // failure is invisible (production 2026-08-08: 'No such image' every loop).
        var harness = new Harness();
        harness.Runtime.PullDigest = (_, _) => "sha256:missing";
        harness.Runtime.MissingDigests.Add("sha256:missing"); // every start throws image-not-found
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: null));

        await harness.Service.RunOnceAsync();

        // The start failed, so there is no container...
        Assert.False(harness.Runtime.Exists("svc-a"));
        var failedOutcome = Assert.Single(harness.Reporter.Outcomes);
        Assert.Equal(ReconcileOutcomeStatus.Failed, failedOutcome.Status);

        // ...yet the heartbeat still lists svc-a as an 'absent' entry carrying the error.
        var entry = HeartbeatEntry(harness, "svc-a");
        Assert.NotNull(entry);
        Assert.Equal(InstanceServicesJson.AbsentState, entry!.State);
        Assert.Null(entry.Digest);
        Assert.False(string.IsNullOrEmpty(entry.LastError));
        Assert.NotNull(entry.LastErrorAt);
    }

    [Fact]
    public async Task Heartbeat_AbsentError_Pruned_WhenServiceRemovedFromManifest()
    {
        // An absent-but-desired service failing to start records an error; once the
        // manifest row is deleted (no container to stop-remove and clear it), the error
        // must not linger forever as a phantom entry.
        var harness = new Harness();
        harness.Runtime.PullDigest = (_, _) => "sha256:missing";
        harness.Runtime.MissingDigests.Add("sha256:missing");
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: null));

        await harness.Service.RunOnceAsync();
        Assert.NotNull(HeartbeatEntry(harness, "svc-a")); // error recorded while desired

        // Remove the service from the manifest and reconcile again.
        await harness.Store.DeleteAsync("svc-a");
        await harness.Service.RunOnceAsync();

        Assert.Null(HeartbeatEntry(harness, "svc-a")); // no phantom absent-error entry
    }

    [Fact]
    public async Task Heartbeat_LastError_TrimmedToBound()
    {
        // Requirement #1: lastError is trimmed to ~300 chars so the jsonb stays small.
        var harness = new Harness();
        harness.Runtime.PullDigest = (_, _) => "sha256:missing";
        harness.Runtime.MissingDigests.Add("sha256:missing");
        // Force a very long failure detail via a long image ref (surfaced in ex.Message).
        var longName = new string('a', 500);
        await harness.Store.UpsertAsync(new ServiceManifest
        {
            Name = "svc-a",
            Image = $"registry/{longName}",
            Tag = "latest",
            Digest = null,
            Port = 8080,
            DesiredStatus = "running",
            IncludeInHealth = true,
            UpdatedBy = "test",
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });

        await harness.Service.RunOnceAsync();

        var entry = HeartbeatEntry(harness, "svc-a");
        Assert.NotNull(entry);
        Assert.NotNull(entry!.LastError);
        Assert.True(entry.LastError!.Length <= InstanceServicesJson.MaxErrorLength);
    }

    // ---------------------------------------------------------------------
    // task #98: reconciler-mode manifest-port fallback suppression.
    // Incident 2026-08-17: ProxyStateService.ResolveCanonical() fell back to the
    // manifest port whenever ServiceHostPortMap had no entry for a service. In
    // reconciler mode that host port is a Docker-assigned ephemeral that can
    // legitimately belong to a sibling service (or nothing), so the gateway
    // silently proxied one service's traffic through a different downstream. The
    // health prober, resolving through the same map+fallback, then reported the
    // misrouted service 'up' — the misrouting was invisible to /api/health.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ReconcilerMode_NoContainerRecorded_PublishesClusterWithNoDestinations()
    {
        // A running-desired service whose canonical container port is not in the
        // map → in reconciler mode the cluster is published with an EMPTY
        // destinations dict, which is YARP's 503 signal. The manifest port must
        // NEVER become the destination fallback here (incident 2026-08-17).
        var harness = new Harness();
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1", port: 8080));

        // What ProxyRouteInitializer does at gateway startup — build routes from
        // the (empty-of-svc-a) map. In production this happens before the
        // reconciler runs, so a service the reconciler has not yet started still
        // gets a route that visibly 503s rather than silently 404ing or proxying
        // to whatever unrelated process holds the manifest port on the host.
        await harness.ProxyState.RefreshRoutesAsync();

        Assert.False(harness.HostPorts.TryGet("svc-a", out _));
        Assert.True(harness.ClusterHasNoDestinations("svc-a"),
            "reconciler mode + no container port must publish an empty-destinations cluster");
        Assert.Null(harness.DestinationFor("svc-a"));
    }

    [Fact]
    public async Task ReconcilerMode_ContainerRecorded_RouteForwardsToRealHostPort()
    {
        // Complement: once the reconciler has recorded the container's host port,
        // the route publishes that address (not the manifest port). Covered by
        // Start_RecordsAssignedHostPort_AndRoutesToIt above, made explicit here as
        // the recovery case paired with the previous test.
        var harness = new Harness();
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1", port: 8080));

        await harness.Service.RunOnceAsync();

        var assigned = harness.Runtime.Get("svc-a")!.HostPort;
        Assert.NotNull(assigned);
        Assert.NotEqual(8080, assigned);
        Assert.Equal($"http://127.0.0.1:{assigned}", harness.DestinationFor("svc-a"));
        Assert.False(harness.ClusterHasNoDestinations("svc-a"));
    }

    [Fact]
    public async Task ProxyOnlyMode_NoContainerRecorded_ManifestPortFallbackPreserved()
    {
        // Requirement: with the reconciler disabled (proxy-only dev mode per the
        // README), the manifest port IS the host port the developer runs the app
        // on locally — the fallback is intentional. Refresh routes on the SAME
        // ProxyStateService with reconciler off and no host-port map entry: the
        // route resolves to the manifest port exactly as before task #98.
        var harness = new Harness(enabled: false);
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1", port: 8080));

        // Build routes directly (the reconciler loop is off in this mode).
        await harness.ProxyState.RefreshRoutesAsync();

        Assert.False(harness.HostPorts.TryGet("svc-a", out _));
        Assert.Equal("http://127.0.0.1:8080", harness.DestinationFor("svc-a"));
    }

    [Fact]
    public async Task ReconcilerMode_SwapOverride_WinsOverMissingContainerPort()
    {
        // Requirement #3: a blue-green destination override is unaffected. Even
        // with NO canonical container-port map entry, the override drives the
        // route destination (rather than 503) — otherwise a fresh swap would
        // fail while the map is empty.
        var harness = new Harness();
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1", port: 8080));

        // No host-port map entry has been set for svc-a.
        await harness.ProxyState.SwapDestinationAsync("svc-a", "http://127.0.0.1:59999");

        Assert.False(harness.HostPorts.TryGet("svc-a", out _));
        Assert.Equal("http://127.0.0.1:59999", harness.DestinationFor("svc-a"));
        Assert.False(harness.ClusterHasNoDestinations("svc-a"));
    }

    [Fact]
    public async Task ReconcilerMode_TransitionIntoNoContainer_LogsOncePerTransition()
    {
        // Requirement #2: log Warning+ ONCE per transition into the no-container
        // state (not every route rebuild), then Info once when the container-truth
        // port reappears. A stable no-container state must not re-warn on every
        // rebuild; otherwise a persistently-broken service floods the log.
        var harness = new Harness();
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1", port: 8080));

        // First route build (production: ProxyRouteInitializer at startup): map is
        // empty → single Warning entered.
        await harness.ProxyState.RefreshRoutesAsync();
        // Second rebuild with the SAME no-container state → no new Warning.
        await harness.ProxyState.RefreshRoutesAsync();

        int WarningsForService() => harness.Logs.Entries.Count(e =>
            e.Level == LogLevel.Warning
            && e.Category.Contains("ProxyStateService", StringComparison.Ordinal)
            && e.Message.Contains("svc-a", StringComparison.Ordinal));

        Assert.Equal(1, WarningsForService());

        // Now the reconciler records the container's real port and rebuilds: a
        // single Info recovery is logged; no additional warnings.
        harness.HostPorts.Set("svc-a", 40_000);
        await harness.ProxyState.RefreshRoutesAsync();

        Assert.Equal(1, WarningsForService()); // still only the original one
        Assert.Contains(harness.Logs.Entries, e =>
            e.Level == LogLevel.Information
            && e.Category.Contains("ProxyStateService", StringComparison.Ordinal)
            && e.Message.Contains("svc-a", StringComparison.Ordinal)
            && e.Message.Contains("resumed", StringComparison.OrdinalIgnoreCase));

        // If svc-a drops offline again later, that IS a fresh transition — the
        // next entry into 'no container' must log a new Warning (not silently be
        // treated as the previous transition still in effect).
        harness.HostPorts.Remove("svc-a");
        await harness.ProxyState.RefreshRoutesAsync();
        Assert.Equal(2, WarningsForService());
    }

    [Fact]
    public async Task StartupOrdering_HostPortMapPrimedBeforeFirstRouteBuild()
    {
        // Requirement #4: on gateway restart with containers already running, the
        // ServiceHostPortMap must be primed from the runtime inventory BEFORE the
        // first route build — otherwise the reconciler-mode no-fallback would
        // publish an empty-destinations cluster (503) even though a healthy
        // container is bound on its ephemeral port. This test simulates a fresh
        // gateway process (in-memory swap state gone) whose startup path runs the
        // ProxyRouteInitializer against a runtime that already has the container.
        var runtime = new FakeContainerRuntime();
        runtime.Seed(new ContainerInfo(
            "svc-a", "registry/svc-a", "sha256:v1", "running",
            DateTimeOffset.UnixEpoch, EmptyEnvHash, HostPort: 8081));
        var store = new InMemoryManifestStore();
        await store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1", port: 8080));

        // Wire the proxy stack in reconciler mode; ProxyRouteInitializer primes
        // the map from the runtime, then builds routes.
        var services = new ServiceCollection();
        services.AddSingleton<IManifestStore>(store);
        services.AddSingleton<IServiceAddressResolver, HostLoopbackAddressResolver>();
        services.AddSingleton<ServiceHostPortMap>();
        services.AddSingleton<ManifestProxyConfigProvider>();
        services.AddSingleton(new ReconcilerOptions { Enabled = true });
        services.AddSingleton<ProxyStateService>();
        services.AddLogging();
        await using var sp = services.BuildServiceProvider();
        var hostPorts = sp.GetRequiredService<ServiceHostPortMap>();
        var state = sp.GetRequiredService<ProxyStateService>();
        var config = sp.GetRequiredService<ManifestProxyConfigProvider>();
        var initializer = new ProxyRouteInitializer(
            state, hostPorts, NullLogger<ProxyRouteInitializer>.Instance, runtime);

        await initializer.StartAsync(CancellationToken.None);

        // Map has the pre-existing container's real host port.
        Assert.True(hostPorts.TryGet("svc-a", out var mapped));
        Assert.Equal(8081, mapped);
        // Route was published against the real port — NOT an empty cluster.
        var cluster = Assert.Single(config.GetConfig().Clusters);
        Assert.NotNull(cluster.Destinations);
        Assert.Equal("http://127.0.0.1:8081", cluster.Destinations!.Values.Single().Address);
    }

    // ---------------------------------------------------------------------
    // task #99: digest-drift circuit breaker + bounded port-map skip.
    // Incident 2026-08-17/18: a service's pinned digest could not be satisfied
    // from its tag (tag overwritten in the registry). The reconciler looped
    // FOREVER, ~every 60-70s: pull → start green by pinned → image-not-found →
    // fallback to pulled → promote → next loop sees digest drift → replace again.
    // Each cycle Docker gave the promoted container a fresh ephemeral port; the
    // service's mid-swap flag kept ServiceHostPortMap skipping it, so the map
    // froze at a port from an early cycle. That port was later reassigned to a
    // sibling service's container and the frozen route silently proxied prod to
    // dev for hours (health returned 200 through the same frozen map).
    // ---------------------------------------------------------------------

    [Fact]
    public async Task DigestDriftBreaker_UnsatisfiablePin_TripsAfterThreeCycles_ThenStable()
    {
        // Requirement #1: after N=3 consecutive digest-drift replaces landing on a
        // digest other than the pin, the breaker trips: no more replaces, the last
        // successfully started container is left serving, degraded is surfaced, and
        // exactly ONE Warning is logged for the trip transition.
        var harness = new Harness();
        harness.Runtime.PullDigest = (_, _) => "sha256:pulled";
        harness.Runtime.MissingDigests.Add("sha256:pinned");
        harness.Prober.Ready = true;
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:pinned", port: 8080));

        // Loop 1: no container → Start (fallback to pulled). Not a BGR — the counter
        // is untouched, so the breaker only starts counting on subsequent replaces.
        await harness.Service.RunOnceAsync();
        Assert.Equal("sha256:pulled", harness.Runtime.Get("svc-a")!.Digest);
        Assert.DoesNotContain(harness.Reporter.Outcomes, o => o.Kind == ReconcileActionKind.BlueGreenReplace);

        // Loops 2, 3, 4: three digest-drift replaces, each landing on pulled again.
        // The breaker trips at the end of loop 4 (cycles == 3).
        var bgrOutcomes = 0;
        for (var i = 0; i < ReconcilerService.DigestDriftBreakerThreshold; i++)
        {
            harness.Reporter.Outcomes.Clear();
            await harness.Service.RunOnceAsync();
            bgrOutcomes += harness.Reporter.Outcomes.Count(o =>
                o.Kind == ReconcileActionKind.BlueGreenReplace && o.Status == ReconcileOutcomeStatus.Succeeded);
        }
        Assert.Equal(ReconcilerService.DigestDriftBreakerThreshold, bgrOutcomes);

        // Loop 5+: breaker tripped, no more replaces run — even after multiple loops.
        var opsBeforeSkippedLoops = harness.Runtime.Operations.Count;
        var portBefore = harness.Runtime.Get("svc-a")!.HostPort;
        for (var i = 0; i < 3; i++)
        {
            await harness.Service.RunOnceAsync();
        }

        var newOps = harness.Runtime.Operations.Skip(opsBeforeSkippedLoops).ToList();
        Assert.DoesNotContain(newOps, op => op.StartsWith("Start:svc-a-green") || op.StartsWith("Rename:"));

        // The last successfully started container is still serving — same port and digest.
        var container = harness.Runtime.Get("svc-a")!;
        Assert.Equal("sha256:pulled", container.Digest);
        Assert.Equal(portBefore, container.HostPort);

        // Route follows the container's real port; the map was never frozen at a
        // port that no longer belongs to svc-a.
        Assert.True(harness.HostPorts.TryGet("svc-a", out var mapped));
        Assert.Equal(container.HostPort, mapped);
        Assert.Equal($"http://127.0.0.1:{container.HostPort}", harness.DestinationFor("svc-a"));

        // The heartbeat entry surfaces the degraded flag (surfacing on /mgmt/services).
        var entry = HeartbeatEntry(harness, "svc-a");
        Assert.NotNull(entry);
        Assert.True(entry!.Degraded);
        Assert.NotNull(entry.LastError);
        Assert.Contains("digest-drift breaker tripped", entry.LastError!, StringComparison.OrdinalIgnoreCase);

        // Exactly ONE Warning-level trip log fired across all loops — not one per loop.
        var tripWarnings = harness.Logs.Entries.Count(e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("Digest-drift breaker tripped", StringComparison.Ordinal)
            && e.Message.Contains("svc-a", StringComparison.Ordinal));
        Assert.Equal(1, tripWarnings);
    }

    [Fact]
    public async Task DigestDriftBreaker_TrippedService_DoesNotFreezeAnotherServicesPort()
    {
        // Requirement #4 (regression proof): a stuck / long-running swap on svc-a
        // must NOT freeze svc-a's route at a host port Docker later reassigned to
        // svc-b's container. Simulates a leaked / long-lived override on svc-a with
        // an aged swap-start stamp, plus svc-b running on the port svc-a's map used
        // to hold, and verifies the bounded skip drops svc-a so container-truth
        // reconciliation moves the route back to svc-a's own real port.
        var harness = new Harness();
        // Both services have running canonical containers with distinct real ports.
        // svc-b's port is the SAME as svc-a's stale map entry — the recycled-port
        // scenario the incident described.
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash, hostPort: 51000));
        harness.Runtime.Seed(Running("svc-b", "sha256:v1", EmptyEnvHash, hostPort: 40000));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1", port: 8080));
        await harness.Store.UpsertAsync(Manifest("svc-b", digest: "sha256:v1", port: 8080));
        // The map for svc-a is stale: it still holds 40000, which now belongs to svc-b.
        harness.HostPorts.Set("svc-a", 40000);
        harness.HostPorts.Set("svc-b", 40000);
        // svc-a is mid-swap (override pointing somewhere), but the swap started long
        // enough ago that the bounded skip must EXCLUDE it — otherwise the map
        // would stay frozen at 40000 and svc-a's route would proxy to svc-b.
        await harness.ProxyState.SwapDestinationAsync("svc-a", "http://127.0.0.1:59999");
        // Advance the clock well past 2 × (readiness + drain) so svc-a's swap age
        // exceeds the bound. With ReadinessTimeout = 50ms and DrainDelay = 0, the
        // bound is 100ms; advance by a minute to be unambiguously past it.
        harness.Clock.Now += TimeSpan.FromMinutes(1);

        await harness.Service.RunOnceAsync();

        // svc-a's map now reflects its OWN container's real port (51000), not the
        // stale 40000 that Docker reassigned to svc-b.
        Assert.True(harness.HostPorts.TryGet("svc-a", out var svcAMapped));
        Assert.Equal(51000, svcAMapped);
        Assert.True(harness.HostPorts.TryGet("svc-b", out var svcBMapped));
        Assert.Equal(40000, svcBMapped);
        // The route CANNOT be latched to svc-b's port for svc-a: it either follows
        // the still-live override (still pointing where the caller set it) or the
        // real svc-a port — never the recycled port belonging to svc-b.
        Assert.NotEqual($"http://127.0.0.1:{svcBMapped}", harness.DestinationFor("svc-a"));
    }

    [Fact]
    public async Task DigestDriftBreaker_NormalSingleSwap_Unaffected()
    {
        // Requirement #4 (c): a normal, single blue-green swap onto a satisfiable
        // pinned digest is not disturbed by the breaker — no degraded flag, no
        // Warning, one clean replace with the container ending on the pin.
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1", EmptyEnvHash, hostPort: 8080));
        harness.Runtime.PullDigest = (_, _) => "sha256:v2";  // pin IS satisfiable
        harness.Prober.Ready = true;
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v2", port: 8080));

        await harness.Service.RunOnceAsync();

        // The container is on the pinned digest — the fallback never fired.
        Assert.Equal("sha256:v2", harness.Runtime.Get("svc-a")!.Digest);
        var outcome = Assert.Single(harness.Reporter.Outcomes);
        Assert.Equal(ReconcileActionKind.BlueGreenReplace, outcome.Kind);
        Assert.Equal(ReconcileOutcomeStatus.Succeeded, outcome.Status);

        // No degraded flag, no breaker Warning.
        var entry = HeartbeatEntry(harness, "svc-a");
        Assert.NotNull(entry);
        Assert.False(entry!.Degraded);
        Assert.Null(entry.LastError);
        Assert.DoesNotContain(harness.Logs.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Digest-drift breaker tripped", StringComparison.Ordinal));

        // A subsequent loop is a pure no-op (converged) — no lingering breaker
        // state, no additional replaces.
        var opsBefore = harness.Runtime.Operations.Count;
        harness.Reporter.Outcomes.Clear();
        await harness.Service.RunOnceAsync();
        Assert.Empty(harness.Runtime.Operations.Skip(opsBefore));
        Assert.Empty(harness.Reporter.Outcomes);
    }

    [Fact]
    public async Task DigestDriftBreaker_NewDeploy_ClearsBreaker_AndReconcilesNormally()
    {
        // Requirement #4 (d): a new deploy (manifest pin changes) resets the
        // breaker so the service reconciles normally on the new pin, degraded
        // clears, and the recovery is announced as an Info transition.
        var harness = new Harness();
        harness.Runtime.PullDigest = (_, _) => "sha256:pulled";
        harness.Runtime.MissingDigests.Add("sha256:pinned");
        harness.Prober.Ready = true;
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:pinned", port: 8080));

        // Trip the breaker: one Start + N digest-drift replaces.
        await harness.Service.RunOnceAsync();
        for (var i = 0; i < ReconcilerService.DigestDriftBreakerThreshold; i++)
        {
            await harness.Service.RunOnceAsync();
        }
        Assert.True(HeartbeatEntry(harness, "svc-a")!.Degraded);

        // A new deploy resolves a fresh, satisfiable pinned digest. Registry is now
        // consistent: pulling yields the same sha as the pin.
        harness.Runtime.PullDigest = (_, _) => "sha256:v3";
        harness.Runtime.MissingDigests.Clear();
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v3", port: 8080));

        var opsBefore = harness.Runtime.Operations.Count;
        harness.Reporter.Outcomes.Clear();

        await harness.Service.RunOnceAsync();

        // A single successful replace ran onto the new pin — no stuck-loop.
        var outcome = Assert.Single(harness.Reporter.Outcomes);
        Assert.Equal(ReconcileActionKind.BlueGreenReplace, outcome.Kind);
        Assert.Equal(ReconcileOutcomeStatus.Succeeded, outcome.Status);
        Assert.Equal("sha256:v3", harness.Runtime.Get("svc-a")!.Digest);

        // The rename op happened (green promoted): the reconciler is unblocked.
        var newOps = harness.Runtime.Operations.Skip(opsBefore).ToList();
        Assert.Contains(newOps, op => op == "Rename:svc-a-green->svc-a");

        // Degraded flag is gone and the sticky lastError is cleared.
        var entry = HeartbeatEntry(harness, "svc-a");
        Assert.NotNull(entry);
        Assert.False(entry!.Degraded);
        Assert.Null(entry.LastError);

        // The recovery (breaker cleared) is announced as an Info transition.
        Assert.Contains(harness.Logs.Entries, e =>
            e.Level == LogLevel.Information
            && e.Message.Contains("Digest-drift breaker for svc-a cleared", StringComparison.Ordinal));

        // A subsequent loop is a pure no-op — the container converged on the new pin.
        var opsAfterDeploy = harness.Runtime.Operations.Count;
        harness.Reporter.Outcomes.Clear();
        await harness.Service.RunOnceAsync();
        Assert.Empty(harness.Runtime.Operations.Skip(opsAfterDeploy));
        Assert.Empty(harness.Reporter.Outcomes);
    }
}
