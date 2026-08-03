using Gateway.Api.Containers;
using Gateway.Api.Data;
using Gateway.Api.Instances;
using Gateway.Api.Manifest;
using Gateway.Api.Proxy;
using Gateway.Api.Reconcile;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Api.Tests;

/// <summary>
/// Persistence tests for the fleet <c>instance_status</c> heartbeat (tech-spec
/// §4.4) against Sqlite in-memory — the build box has no Postgres. Covers the EF
/// store's upsert + stale cleanup directly, and the reconciler driving the whole
/// heartbeat path (upsert every loop; leader-only stale cleanup) through a real
/// Sqlite-backed store.
/// </summary>
public class InstanceStatusStoreTests
{
    private static InstanceStatus Row(string id, DateTimeOffset heartbeat) => new()
    {
        InstanceId = id,
        PrivateIp = "10.0.0.1",
        GatewayVer = "1.2.3",
        IsLeader = false,
        Services = "[]",
        HeartbeatAt = heartbeat,
    };

    [Fact]
    public async Task Upsert_InsertsThenUpdates_SameRow()
    {
        using var db = NewContext(out var conn);
        using (conn)
        {
            db.Database.EnsureCreated();
            var store = new EfInstanceStatusStore(db);

            var now = DateTimeOffset.UtcNow;
            await store.UpsertAsync(Row("i-1", now));

            var updated = Row("i-1", now.AddSeconds(30));
            updated.IsLeader = true;
            updated.Services = "[{\"name\":\"svc-a\"}]";
            await store.UpsertAsync(updated);

            db.ChangeTracker.Clear();
            var saved = Assert.Single(db.InstanceStatus.ToList());
            Assert.Equal("i-1", saved.InstanceId);
            Assert.True(saved.IsLeader);
            Assert.Contains("svc-a", saved.Services);
        }
    }

    [Fact]
    public async Task DeleteStale_RemovesOnlyRowsOlderThanCutoff()
    {
        using var db = NewContext(out var conn);
        using (conn)
        {
            db.Database.EnsureCreated();
            var store = new EfInstanceStatusStore(db);

            var now = DateTimeOffset.UtcNow;
            await store.UpsertAsync(Row("i-fresh", now));
            await store.UpsertAsync(Row("i-stale", now - TimeSpan.FromMinutes(5)));

            var removed = await store.DeleteStaleAsync(now - TimeSpan.FromSeconds(90));

            Assert.Equal(1, removed);
            db.ChangeTracker.Clear();
            var remaining = db.InstanceStatus.Select(x => x.InstanceId).ToList();
            Assert.Equal(new[] { "i-fresh" }, remaining);
        }
    }

    [Fact]
    public async Task Reconciler_Heartbeat_PersistsRow_ViaSqlite()
    {
        using var fixture = new SqliteReconcilerFixture(isLeader: true);
        // A converged container (its env hash matches the empty desired env) so the
        // reconcile pass is a no-op and the heartbeat reports it verbatim.
        var envHash = EnvHasher.Compute(new Dictionary<string, string>());
        fixture.Runtime.Seed(new ContainerInfo(
            "svc-a", "registry/svc-a", "sha256:v1", "running", DateTimeOffset.UnixEpoch, envHash, Restarts: 2));
        await fixture.Manifests.UpsertAsync(new ServiceManifest
        {
            Name = "svc-a",
            Image = "registry/svc-a",
            Tag = "latest",
            Digest = "sha256:v1",
            Port = 8080,
            DesiredStatus = "running",
            IncludeInHealth = true,
            UpdatedBy = "test",
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });

        await fixture.Service.RunOnceAsync();

        using var verify = fixture.NewVerificationContext();
        var row = Assert.Single(verify.InstanceStatus.ToList());
        Assert.Equal("i-test", row.InstanceId);
        Assert.Equal("1.2.3.4", row.PrivateIp);
        Assert.True(row.IsLeader);
        Assert.Contains("\"name\":\"svc-a\"", row.Services);
        Assert.Contains("\"restarts\":2", row.Services);
    }

    [Fact]
    public async Task Reconciler_Leader_PrunesStaleRow_ViaSqlite()
    {
        using var fixture = new SqliteReconcilerFixture(isLeader: true);
        fixture.SeedRow(Row("i-gone", DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5)));

        await fixture.Service.RunOnceAsync();

        using var verify = fixture.NewVerificationContext();
        var ids = verify.InstanceStatus.Select(x => x.InstanceId).ToList();
        Assert.DoesNotContain("i-gone", ids);
        Assert.Contains("i-test", ids);
    }

    [Fact]
    public async Task Reconciler_NonLeader_LeavesStaleRow_ViaSqlite()
    {
        using var fixture = new SqliteReconcilerFixture(isLeader: false);
        fixture.SeedRow(Row("i-gone", DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5)));

        await fixture.Service.RunOnceAsync();

        using var verify = fixture.NewVerificationContext();
        var ids = verify.InstanceStatus.Select(x => x.InstanceId).ToList();
        Assert.Contains("i-gone", ids); // non-leader must not prune
        Assert.Contains("i-test", ids);
    }

    private static GatewayDbContext NewContext(out SqliteConnection conn)
    {
        conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        return new GatewayDbContext(OptionsFor(conn));
    }

    private static DbContextOptions<GatewayDbContext> OptionsFor(SqliteConnection conn) =>
        new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite(conn)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

    /// <summary>
    /// A reconciler wired to a real Sqlite-backed <see cref="EfInstanceStatusStore"/>
    /// (scoped, like production) plus fakes for everything else, so the heartbeat
    /// path is exercised end-to-end against a database.
    /// </summary>
    private sealed class SqliteReconcilerFixture : IDisposable
    {
        private readonly SqliteConnection _conn;
        private readonly ServiceProvider _provider;

        public FakeContainerRuntime Runtime { get; } = new();
        public InMemoryManifestStore Manifests { get; } = new();
        public ReconcilerService Service { get; }

        public SqliteReconcilerFixture(bool isLeader)
        {
            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();

            using (var seedCtx = new GatewayDbContext(OptionsFor(_conn)))
            {
                seedCtx.Database.EnsureCreated();
            }

            var services = new ServiceCollection();
            services.AddDbContext<GatewayDbContext>(o => o
                .UseSqlite(_conn)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
            services.AddScoped<IInstanceStatusStore, EfInstanceStatusStore>();
            services.AddSingleton<IManifestStore>(Manifests);
            services.AddSingleton<IServiceEnvProvider, NullServiceEnvProvider>();
            services.AddSingleton<IServiceAddressResolver, ContainerDnsAddressResolver>();
            services.AddSingleton<ManifestProxyConfigProvider>();
            services.AddSingleton<ProxyStateService>();
            _provider = services.BuildServiceProvider();

            var metadata = new InstanceMetadataProvider(new IInstanceMetadata[]
            {
                new StubInstanceMetadata(new InstanceIdentity("i-test", "1.2.3.4", null)),
            });

            Service = new ReconcilerService(
                Runtime,
                _provider.GetRequiredService<ProxyStateService>(),
                _provider.GetRequiredService<IServiceAddressResolver>(),
                _provider.GetRequiredService<IServiceScopeFactory>(),
                new AlwaysReadyProber(),
                new NullReporter(),
                metadata,
                new InMemoryLeaderElection(isLeader),
                new ReconcilerOptions { Enabled = true },
                NullLogger<ReconcilerService>.Instance);
        }

        public void SeedRow(InstanceStatus row)
        {
            using var ctx = new GatewayDbContext(OptionsFor(_conn));
            ctx.InstanceStatus.Add(row);
            ctx.SaveChanges();
        }

        public GatewayDbContext NewVerificationContext() => new(OptionsFor(_conn));

        public void Dispose()
        {
            _provider.Dispose();
            _conn.Dispose();
        }
    }

    private sealed class AlwaysReadyProber : IReadinessProber
    {
        public Task<bool> WaitForReadyAsync(string address, string healthPath, TimeSpan timeout, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class NullReporter : IReconcileReporter
    {
        public Task ReportAsync(ReconcileOutcome outcome, CancellationToken ct = default) => Task.CompletedTask;
    }
}
