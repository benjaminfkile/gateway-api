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
/// Tests the reconciler's image-prune housekeeping (tech-spec §4.3): at the end of
/// every reconcile pass the node prunes stale local images so per-deploy layers do
/// not fill the root volume. Runs on every instance (leader-only would leave
/// followers to bloat), throttled to at most once per <see cref="ReconcilerOptions.ImagePruneInterval"/>,
/// and any prune failure is swallowed so it can never break the reconcile pass.
/// </summary>
public class ReconcilerImagePruneTests
{
    private static readonly string EmptyEnvHash = EnvHasher.Compute(new Dictionary<string, string>());

    private sealed class FakeReadinessProber : IReadinessProber
    {
        public bool Ready { get; set; } = true;

        public Task<bool> WaitForReadyAsync(string address, string healthPath, TimeSpan timeout, CancellationToken ct = default) =>
            Task.FromResult(Ready);
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

    /// <summary>Captures every ILogger call so tests can assert log level + message.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);
        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly List<(LogLevel, string, Exception?)> _entries;

            public CapturingLogger(List<(LogLevel, string, Exception?)> entries)
            {
                _entries = entries;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (_entries)
                {
                    _entries.Add((logLevel, formatter(state, exception), exception));
                }
            }
        }
    }

    private sealed class Harness
    {
        public InMemoryManifestStore Store { get; } = new();
        public FakeContainerRuntime Runtime { get; } = new();
        public FakeInstanceStatusStore StatusStore { get; } = new();
        public FakeDeployStore DeployStore { get; } = new();
        public FakeReadinessProber Prober { get; } = new();
        public CapturingLoggerProvider Logs { get; } = new();
        public ReconcilerOptions Options { get; }
        public ReconcilerService Service { get; }

        public Harness(
            bool isLeader = true,
            bool imagePruneEnabled = true,
            TimeSpan? imagePruneInterval = null,
            TimeSpan? imagePruneAge = null,
            int? imagePruneKeep = null)
        {
            Options = new ReconcilerOptions
            {
                Enabled = true,
                DrainDelay = TimeSpan.Zero,
                ReadinessTimeout = TimeSpan.FromMilliseconds(10),
                ReadinessPollInterval = TimeSpan.FromMilliseconds(1),
                ImagePruneEnabled = imagePruneEnabled,
                ImagePruneInterval = imagePruneInterval ?? TimeSpan.FromHours(1),
                ImagePruneAge = imagePruneAge ?? TimeSpan.FromHours(48),
                ImagePruneKeep = imagePruneKeep ?? 3,
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
            services.AddSingleton(Options);
            services.AddLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Debug);
                logging.AddProvider(Logs);
            });
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
                Prober,
                new NullReporter(),
                metadata,
                new InMemoryLeaderElection(isLeader),
                new LeadershipState(),
                Options,
                provider.GetRequiredService<ILogger<ReconcilerService>>());
        }
    }

    private static ServiceManifest Manifest(string name, string? digest = "sha256:v1") => new()
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

    private static ContainerInfo Running(string name, string digest, int hostPort = 8080) =>
        new(name, $"registry/{name}", digest, "running", DateTimeOffset.UnixEpoch, EmptyEnvHash, HostPort: hostPort);

    private static ContainerInfo Stopped(string name, string digest, int hostPort = 8080) =>
        new(name, $"registry/{name}", digest, "exited", DateTimeOffset.UnixEpoch, EmptyEnvHash, HostPort: hostPort);

    [Fact]
    public async Task Prune_RunsAtEndOfPass_AndPassesOptionsThrough()
    {
        var harness = new Harness(imagePruneAge: TimeSpan.FromHours(72), imagePruneKeep: 5);
        harness.Runtime.Seed(Running("svc-a", "sha256:running"));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:running"));

        await harness.Service.RunOnceAsync();

        var call = Assert.Single(harness.Runtime.PruneCalls);
        Assert.Equal(TimeSpan.FromHours(72), call.MinimumAge);
        Assert.Equal(5, call.KeepPerRepository);
    }

    [Fact]
    public async Task Prune_KeepSet_IncludesRunningContainerDigest()
    {
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:running-a"));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:running-a"));

        await harness.Service.RunOnceAsync();

        var call = Assert.Single(harness.Runtime.PruneCalls);
        Assert.Contains("sha256:running-a", call.KeepDigests);
    }

    [Fact]
    public async Task Prune_KeepSet_IncludesStoppedContainerDigest()
    {
        var harness = new Harness();
        harness.Runtime.Seed(Stopped("svc-a", "sha256:stopped-a"));
        // Manifest also says stopped so the reconciler does not remove the container.
        var manifest = Manifest("svc-a", digest: "sha256:stopped-a");
        manifest.DesiredStatus = "stopped";
        await harness.Store.UpsertAsync(manifest);

        await harness.Service.RunOnceAsync();

        var call = Assert.Single(harness.Runtime.PruneCalls);
        Assert.Contains("sha256:stopped-a", call.KeepDigests);
    }

    [Fact]
    public async Task Prune_KeepSet_IncludesGreenCandidateDigest_MidBlueGreen()
    {
        // The green candidate is a canonical managed container mid blue-green: its
        // digest must appear in the keep set so its image is never pruned mid swap.
        // Seed a '-green' container directly (as if a swap is in flight) alongside
        // the old canonical container so the plan produces no action this pass.
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1"));
        harness.Runtime.Seed(Running("svc-a-green", "sha256:green-cand"));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1"));

        await harness.Service.RunOnceAsync();

        var call = Assert.Single(harness.Runtime.PruneCalls);
        Assert.Contains("sha256:v1", call.KeepDigests);
        Assert.Contains("sha256:green-cand", call.KeepDigests);
    }

    [Fact]
    public async Task Prune_KeepSet_IncludesManifestPinnedDigest_WithNoRunningContainer()
    {
        // Requirement: even when the service has no container yet (or its container
        // is not up), the pinned digest is protected so the rollback target survives.
        // Use a manifest pin whose image is missing locally: the start fails, no
        // container exists, but the pin still lands in the keep set.
        var harness = new Harness();
        harness.Runtime.MissingDigests.Add("sha256:pin-only");
        harness.Runtime.PullDigest = (_, _) => "sha256:pin-only";
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:pin-only"));

        await harness.Service.RunOnceAsync();

        Assert.False(harness.Runtime.Exists("svc-a"));
        var call = Assert.Single(harness.Runtime.PruneCalls);
        Assert.Contains("sha256:pin-only", call.KeepDigests);
    }

    [Fact]
    public async Task Prune_KeepSet_IncludesPreviousDeployDigest_FromHistory()
    {
        // A completed deploy landed on sha256:v1 in the past; the current pin is v2.
        // The reconciler must include the previous ToDigest so a rollback is instant.
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v2"));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v2"));
        await harness.DeployStore.AddAsync(new DeployHistory
        {
            Service = "svc-a",
            FromDigest = null,
            ToDigest = "sha256:v1",
            Actor = "bob",
            Action = DeployAction.Deploy,
            Status = DeployStatus.Done,
            StartedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(2),
            FinishedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(2),
        });
        await harness.DeployStore.AddAsync(new DeployHistory
        {
            Service = "svc-a",
            FromDigest = "sha256:v1",
            ToDigest = "sha256:v2",
            Actor = "bob",
            Action = DeployAction.Deploy,
            Status = DeployStatus.Done,
            StartedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(1),
            FinishedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(1),
        });

        await harness.Service.RunOnceAsync();

        var call = Assert.Single(harness.Runtime.PruneCalls);
        Assert.Contains("sha256:v2", call.KeepDigests); // current pin
        Assert.Contains("sha256:v1", call.KeepDigests); // previous deploy — rollback target
    }

    [Fact]
    public async Task Prune_ThrottledAcrossConsecutivePasses_ThenRunsAgainAfterInterval()
    {
        // ImagePruneInterval throttles the prune to at most one call per window; the
        // first pass after startup runs immediately, subsequent passes within the
        // window are skipped, and once the interval elapses the next pass runs again.
        var harness = new Harness(imagePruneInterval: TimeSpan.FromMinutes(30));
        harness.Runtime.Seed(Running("svc-a", "sha256:v1"));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1"));

        // Pass 1: prune runs (first-ever, no throttle).
        await harness.Service.RunOnceAsync();
        Assert.Single(harness.Runtime.PruneCalls);

        // Passes 2, 3, 4: still within the interval — no additional prunes.
        await harness.Service.RunOnceAsync();
        await harness.Service.RunOnceAsync();
        await harness.Service.RunOnceAsync();
        Assert.Single(harness.Runtime.PruneCalls);

        // Force the last-run stamp back so the next pass sees the interval elapsed.
        harness.Service._lastImagePruneAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        await harness.Service.RunOnceAsync();
        Assert.Equal(2, harness.Runtime.PruneCalls.Count);
    }

    [Fact]
    public async Task Prune_Disabled_NeverRuns()
    {
        var harness = new Harness(imagePruneEnabled: false);
        harness.Runtime.Seed(Running("svc-a", "sha256:v1"));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1"));

        await harness.Service.RunOnceAsync();
        await harness.Service.RunOnceAsync();

        Assert.Empty(harness.Runtime.PruneCalls);
    }

    [Fact]
    public async Task Prune_Throws_ReconcilePassStillSucceeds_AndLogsWarning()
    {
        // A prune failure logs one Warning and is swallowed. The reconcile pass must
        // still converge the container fleet — no exception escapes RunOnceAsync.
        var harness = new Harness();
        harness.Runtime.NextPruneThrow = new InvalidOperationException("docker off");
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1"));

        await harness.Service.RunOnceAsync();

        // Container converged despite the prune throw.
        Assert.True(harness.Runtime.Exists("svc-a"));
        // The prune throw was recorded exactly once, and a Warning was logged.
        Assert.Single(harness.Runtime.PruneCalls);
        Assert.Contains(harness.Logs.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("Image prune", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Prune_NonLeader_StillRunsOnEveryInstance()
    {
        // Disk is per box, so image prune runs on every instance — NOT leader-only.
        var harness = new Harness(isLeader: false);
        harness.Runtime.Seed(Running("svc-a", "sha256:v1"));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1"));

        await harness.Service.RunOnceAsync();

        Assert.Single(harness.Runtime.PruneCalls);
    }

    [Fact]
    public async Task Prune_DeletedZero_LogsAtDebug_NotInformation()
    {
        // A steady fleet with no candidates to delete must not emit an hourly
        // Information line — a deleted-count of zero logs at Debug instead.
        var harness = new Harness();
        harness.Runtime.Seed(Running("svc-a", "sha256:v1"));
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1"));

        await harness.Service.RunOnceAsync();

        // The fake ran the prune and reported zero deletions.
        var result = Assert.Single(harness.Runtime.PruneCalls);
        Assert.NotNull(result);

        Assert.DoesNotContain(harness.Logs.Entries, e =>
            e.Level == LogLevel.Information
            && e.Message.Contains("Image prune", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(harness.Logs.Entries, e =>
            e.Level == LogLevel.Debug
            && e.Message.Contains("Image prune", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Prune_DeletedNonZero_LogsAtInformation_WithCountAndBytes()
    {
        // A pass that actually removed images emits one Information line carrying
        // the deleted count and the bytes reclaimed. Set keep-per-repo=0 so nothing
        // shields these unreferenced legacy images from the age-based sweep.
        var harness = new Harness(imagePruneKeep: 0);
        var now = DateTime.UtcNow;
        harness.Runtime.UtcNow = () => now;
        harness.Runtime.SetImages(new[]
        {
            new FakeContainerRuntime.FakeImage(
                Id: "sha256:stale-1",
                RepoTags: new[] { "registry/legacy:v1" },
                RepoDigests: new[] { "registry/legacy@sha256:stale-1" },
                Created: now - TimeSpan.FromDays(30),
                Size: 12_345_678L),
            new FakeContainerRuntime.FakeImage(
                Id: "sha256:stale-2",
                RepoTags: new[] { "registry/legacy:v2" },
                RepoDigests: new[] { "registry/legacy@sha256:stale-2" },
                Created: now - TimeSpan.FromDays(29),
                Size: 1_000L),
        });
        await harness.Store.UpsertAsync(Manifest("svc-a", digest: "sha256:v1"));

        await harness.Service.RunOnceAsync();

        var infoLine = Assert.Single(harness.Logs.Entries, e =>
            e.Level == LogLevel.Information
            && e.Message.Contains("Image prune", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("2", infoLine.Message); // count
        Assert.Contains("12346678", infoLine.Message); // total bytes reclaimed
    }
}
