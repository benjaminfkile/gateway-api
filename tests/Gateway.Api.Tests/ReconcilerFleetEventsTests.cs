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
/// Tests the reconciler's <c>ops:fleet</c> broadcasts (tech-spec §4.3, §4.4; task #588):
/// the leader publishes a per-cycle <c>heartbeat</c>, a <c>leaderChange</c> on the flip
/// into leadership, and an <c>instances</c> event when membership churns; every instance
/// publishes a <c>serviceError</c> only when its last-error state transitions. All sends
/// ride <see cref="IChannelEventPublisher.TryPublish"/> so a backplane blip is harmless.
/// </summary>
public class ReconcilerFleetEventsTests
{
    private sealed class FakeReadinessProber : IReadinessProber
    {
        public Task<bool> WaitForReadyAsync(string address, string healthPath, TimeSpan timeout, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    /// <summary>An env provider that resolves to empty env, or throws a ref-only error
    /// while <see cref="Throw"/> is set — modelling a missing/denied secret (tech-spec §8).</summary>
    private sealed class ToggleEnvProvider : IServiceEnvProvider
    {
        public bool Throw { get; set; }

        public Task<IReadOnlyDictionary<string, string>> GetEnvAsync(ServiceManifest manifest, CancellationToken ct = default) =>
            Throw
                ? throw new InvalidOperationException($"secret ref for {manifest.Name} not found")
                : Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    }

    private sealed class NullReporter : IReconcileReporter
    {
        public Task ReportAsync(ReconcileOutcome outcome, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A published envelope: the channel, event name, and its anonymous data object.</summary>
    private sealed record Published(string Channel, string Event, object Data)
    {
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

        public IEnumerable<Published> OfEvent(string @event) => Published.Where(p => p.Event == @event);
    }

    private sealed class Harness
    {
        public InMemoryManifestStore Store { get; } = new();
        public FakeContainerRuntime Runtime { get; } = new();
        public FakeInstanceStatusStore StatusStore { get; } = new();
        public ToggleEnvProvider EnvProvider { get; } = new();
        public RecordingPublisher Publisher { get; } = new();
        public ReconcilerService Service { get; }

        public Harness(bool isLeader = true, string instanceId = "i-test")
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
            services.AddSingleton<IServiceEnvProvider>(EnvProvider);
            services.AddSingleton<IInstanceStatusStore>(StatusStore);
            services.AddSingleton<IServiceAddressResolver, HostLoopbackAddressResolver>();
            services.AddSingleton<ServiceHostPortMap>();
            services.AddSingleton<ManifestProxyConfigProvider>();
            services.AddSingleton<ProxyStateService>();
            var provider = services.BuildServiceProvider();

            var metadata = new InstanceMetadataProvider(new IInstanceMetadata[]
            {
                new StubInstanceMetadata(new InstanceIdentity(instanceId, "10.0.0.9", null)),
            });

            Service = new ReconcilerService(
                Runtime,
                provider.GetRequiredService<ProxyStateService>(),
                provider.GetRequiredService<IServiceAddressResolver>(),
                provider.GetRequiredService<ServiceHostPortMap>(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                new FakeReadinessProber(),
                new NullReporter(),
                metadata,
                new InMemoryLeaderElection(isLeader),
                options,
                NullLogger<ReconcilerService>.Instance,
                migrationGate: null,
                publisher: Publisher);
        }
    }

    private static ServiceManifest Manifest(string name) => new()
    {
        Name = name,
        Image = $"registry/{name}",
        Tag = "latest",
        Digest = "sha256:v1",
        Port = 8080,
        DesiredStatus = "running",
        IncludeInHealth = true,
        UpdatedBy = "test",
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task Leader_PublishesHeartbeat_EachCycle_WithContractShape()
    {
        var harness = new Harness(isLeader: true);

        await harness.Service.RunOnceAsync();

        var heartbeat = Assert.Single(harness.Publisher.OfEvent("heartbeat"));
        Assert.Equal(ManagementEndpoints.OpsFleetChannel, heartbeat.Channel);
        // leaderInstanceId is this instance; instanceCount counts the one live row (itself).
        Assert.Equal("i-test", heartbeat.Get("leaderInstanceId"));
        Assert.Equal(1, heartbeat.Get("instanceCount"));
        // ts is a round-trippable ISO 8601 string.
        var ts = Assert.IsType<string>(heartbeat.Get("ts"));
        Assert.True(DateTimeOffset.TryParse(ts, out _));

        // A second cycle heartbeats again (it is a per-cycle liveness signal, not one-shot).
        await harness.Service.RunOnceAsync();
        Assert.Equal(2, harness.Publisher.OfEvent("heartbeat").Count());
    }

    [Fact]
    public async Task NonLeader_PublishesNothing()
    {
        var harness = new Harness(isLeader: false);

        await harness.Service.RunOnceAsync();

        // A follower heartbeats its own row to the DB but broadcasts no fleet event.
        Assert.Single(harness.StatusStore.Upserts);
        Assert.Empty(harness.Publisher.Published);
    }

    [Fact]
    public async Task Leader_PublishesLeaderChange_OnlyOnTransitionIntoLeadership()
    {
        var harness = new Harness(isLeader: true);

        await harness.Service.RunOnceAsync();

        // First loop as leader: announce this instance.
        var change = Assert.Single(harness.Publisher.OfEvent("leaderChange"));
        Assert.Equal(ManagementEndpoints.OpsFleetChannel, change.Channel);
        Assert.Equal("i-test", change.Get("instanceId"));

        // Staying leader must not re-announce on later loops.
        await harness.Service.RunOnceAsync();
        Assert.Single(harness.Publisher.OfEvent("leaderChange"));
    }

    [Fact]
    public async Task Leader_PublishesInstances_WhenStaleRowPruned()
    {
        var harness = new Harness(isLeader: true);
        // A departed instance whose heartbeat aged out well past the stale threshold.
        harness.StatusStore.Seed(ManagementTestData.Instance(
            "i-dead", heartbeatAt: DateTimeOffset.UtcNow - TimeSpan.FromHours(1)));

        await harness.Service.RunOnceAsync();

        var instances = Assert.Single(harness.Publisher.OfEvent("instances"));
        Assert.Equal(ManagementEndpoints.OpsFleetChannel, instances.Channel);
        var pruned = Assert.IsAssignableFrom<IEnumerable<string>>(instances.Get("pruned"));
        Assert.Equal(new[] { "i-dead" }, pruned);
        // The first leader observation seeds membership silently, so nothing is "joined".
        var joined = Assert.IsAssignableFrom<IEnumerable<string>>(instances.Get("joined"));
        Assert.Empty(joined);
    }

    [Fact]
    public async Task Leader_OmitsInstances_WhenMembershipUnchanged()
    {
        var harness = new Harness(isLeader: true);

        // First loop seeds membership (this instance only); no churn to report.
        await harness.Service.RunOnceAsync();
        // A steady second loop with the same single-instance fleet must emit no event.
        await harness.Service.RunOnceAsync();

        Assert.Empty(harness.Publisher.OfEvent("instances"));
    }

    [Fact]
    public async Task Leader_PublishesInstances_WhenNewInstanceJoins()
    {
        var harness = new Harness(isLeader: true);

        // First loop seeds membership with just this instance.
        await harness.Service.RunOnceAsync();
        // A new live instance appears in the inventory before the next loop.
        harness.StatusStore.Seed(ManagementTestData.Instance(
            "i-new", heartbeatAt: DateTimeOffset.UtcNow));

        await harness.Service.RunOnceAsync();

        var instances = Assert.Single(harness.Publisher.OfEvent("instances"));
        var joined = Assert.IsAssignableFrom<IEnumerable<string>>(instances.Get("joined"));
        Assert.Equal(new[] { "i-new" }, joined);
        var pruned = Assert.IsAssignableFrom<IEnumerable<string>>(instances.Get("pruned"));
        Assert.Empty(pruned);
    }

    [Fact]
    public async Task ServiceError_Published_OnSetAndClear_TransitionsOnly()
    {
        var harness = new Harness(isLeader: true);
        await harness.Store.UpsertAsync(Manifest("svc-a"));

        // Loop 1: env fails to resolve → a serviceError is SET with a non-null message.
        harness.EnvProvider.Throw = true;
        await harness.Service.RunOnceAsync();

        var set = Assert.Single(harness.Publisher.OfEvent("serviceError"));
        Assert.Equal(ManagementEndpoints.OpsFleetChannel, set.Channel);
        Assert.Equal("svc-a", set.Get("service"));
        Assert.Equal("i-test", set.Get("instanceId"));
        Assert.NotNull(set.Get("lastError"));
        Assert.IsType<string>(set.Get("lastErrorAt"));

        // Loop 2: still failing with the SAME message → no re-broadcast (transitions only).
        await harness.Service.RunOnceAsync();
        Assert.Single(harness.Publisher.OfEvent("serviceError"));

        // Loop 3: env resolves → the error CLEARS, broadcast with null lastError/lastErrorAt.
        harness.EnvProvider.Throw = false;
        await harness.Service.RunOnceAsync();

        var events = harness.Publisher.OfEvent("serviceError").ToList();
        Assert.Equal(2, events.Count);
        var clear = events[1];
        Assert.Equal("svc-a", clear.Get("service"));
        Assert.Null(clear.Get("lastError"));
        Assert.Null(clear.Get("lastErrorAt"));
    }

    /// <summary>Wraps a real store but throws on the first <c>UpsertAsync</c> only —
    /// models a heartbeat that fails mid-loop before the fleet events are published.</summary>
    private sealed class ThrowOnceStatusStore : IInstanceStatusStore
    {
        private readonly FakeInstanceStatusStore _inner = new();
        private int _upserts;

        public Task UpsertAsync(InstanceStatus status, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _upserts) == 1)
            {
                throw new InvalidOperationException("heartbeat store unavailable");
            }

            return _inner.UpsertAsync(status, ct);
        }

        public Task<IReadOnlyList<InstanceStatus>> GetAllAsync(CancellationToken ct = default) =>
            _inner.GetAllAsync(ct);

        public Task<int> DeleteStaleAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
            _inner.DeleteStaleAsync(cutoff, ct);
    }

    [Fact]
    public async Task Leader_PublishesLeaderChange_AfterThrowingFirstCycle()
    {
        // Task #608 finding 4: if the heartbeat body throws before PublishFleetEventsAsync
        // runs, the leaderChange edge must NOT be consumed — the next successful cycle must
        // still announce this instance as leader (rather than swallowing it for the term).
        var options = new ReconcilerOptions { Enabled = true };
        var services = new ServiceCollection();
        services.AddSingleton<IManifestStore>(new InMemoryManifestStore());
        services.AddSingleton<IServiceEnvProvider, ToggleEnvProvider>();
        services.AddSingleton<IInstanceStatusStore>(new ThrowOnceStatusStore());
        services.AddSingleton<IServiceAddressResolver, HostLoopbackAddressResolver>();
        services.AddSingleton<ServiceHostPortMap>();
        services.AddSingleton<ManifestProxyConfigProvider>();
        services.AddSingleton<ProxyStateService>();
        var provider = services.BuildServiceProvider();
        var metadata = new InstanceMetadataProvider(new IInstanceMetadata[]
        {
            new StubInstanceMetadata(new InstanceIdentity("i-test", "10.0.0.9", null)),
        });
        var publisher = new RecordingPublisher();
        var service = new ReconcilerService(
            new FakeContainerRuntime(),
            provider.GetRequiredService<ProxyStateService>(),
            provider.GetRequiredService<IServiceAddressResolver>(),
            provider.GetRequiredService<ServiceHostPortMap>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeReadinessProber(),
            new NullReporter(),
            metadata,
            new InMemoryLeaderElection(true),
            options,
            NullLogger<ReconcilerService>.Instance,
            migrationGate: null,
            publisher: publisher);

        // First cycle: the heartbeat upsert throws, so no leaderChange is published and the
        // edge is left unconsumed.
        await service.RunOnceAsync();
        Assert.Empty(publisher.OfEvent("leaderChange"));

        // Second cycle succeeds: the retained edge now fires the leaderChange.
        await service.RunOnceAsync();
        var change = Assert.Single(publisher.OfEvent("leaderChange"));
        Assert.Equal("i-test", change.Get("instanceId"));
    }

    [Fact]
    public async Task ThrowingPublisher_DoesNotBreakReconcile()
    {
        // A real publisher over a hub that always throws — a backplane outage on the leader.
        var throwing = new ChannelEventPublisher(
            new FakeGatewayHubContext { SendError = new InvalidOperationException("backplane down") },
            NullLogger<ChannelEventPublisher>.Instance);

        var options = new ReconcilerOptions { Enabled = true };
        var services = new ServiceCollection();
        var store = new InMemoryManifestStore();
        var statusStore = new FakeInstanceStatusStore();
        services.AddSingleton<IManifestStore>(store);
        services.AddSingleton<IServiceEnvProvider, ToggleEnvProvider>();
        services.AddSingleton<IInstanceStatusStore>(statusStore);
        services.AddSingleton<IServiceAddressResolver, HostLoopbackAddressResolver>();
        services.AddSingleton<ServiceHostPortMap>();
        services.AddSingleton<ManifestProxyConfigProvider>();
        services.AddSingleton<ProxyStateService>();
        var provider = services.BuildServiceProvider();
        var metadata = new InstanceMetadataProvider(new IInstanceMetadata[]
        {
            new StubInstanceMetadata(new InstanceIdentity("i-test", "10.0.0.9", null)),
        });
        var service = new ReconcilerService(
            new FakeContainerRuntime(),
            provider.GetRequiredService<ProxyStateService>(),
            provider.GetRequiredService<IServiceAddressResolver>(),
            provider.GetRequiredService<ServiceHostPortMap>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeReadinessProber(),
            new NullReporter(),
            metadata,
            new InMemoryLeaderElection(true),
            options,
            NullLogger<ReconcilerService>.Instance,
            migrationGate: null,
            publisher: throwing);

        // Every ops:fleet TryPublish throws inside the hub; the reconcile still heartbeats.
        await service.RunOnceAsync();

        Assert.Single(statusStore.Upserts);
    }
}
