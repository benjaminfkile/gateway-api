using Gateway.Api.Data;
using Gateway.Api.Management;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Gateway.Api.Tests;

/// <summary>
/// The <see cref="IJobStore"/> contract, run against both the Sqlite-backed
/// <see cref="EfJobStore"/> and <see cref="FakeJobStore"/> so the fake the executor
/// tests rely on behaves like the real store: one active run per job, conditional
/// cancel, last-run lookup, and run history that survives a delete.
/// </summary>
public class JobStoreTests
{
    public static TheoryData<string> Stores => new() { "ef", "fake" };

    private sealed class StoreHandle : IDisposable
    {
        private readonly SqliteConnection? _connection;
        private readonly GatewayDbContext? _db;

        public StoreHandle(string kind)
        {
            if (kind == "fake")
            {
                Store = new FakeJobStore();
                return;
            }

            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            _db = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>()
                .UseSqlite(_connection)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options);
            _db.Database.EnsureCreated();
            Store = new EfJobStore(_db);
        }

        public IJobStore Store { get; }

        public void Dispose()
        {
            _db?.Dispose();
            _connection?.Dispose();
        }
    }

    private static Job NewJob(string name) => new()
    {
        Name = name,
        Schedule = "0 4 * * *",
        TimeZone = "UTC",
        Script = "echo hi",
        ScriptVersion = 1,
        Image = "gateway-job-runner",
        Tag = "latest",
        TimeoutSeconds = 3600,
        Enabled = true,
        UpdatedBy = "seed",
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static JobRun NewRun(string job) => new()
    {
        Job = job,
        Trigger = JobRunTrigger.Manual,
        Actor = "alice",
        Status = JobRunStatus.Queued,
        ScriptVersion = 1,
        QueuedAt = DateTimeOffset.UnixEpoch,
    };

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Enqueue_AdmitsOneActiveRunPerJob(string kind)
    {
        using var handle = new StoreHandle(kind);
        var store = handle.Store;
        await store.UpsertAsync(NewJob("nightly"));
        await store.UpsertAsync(NewJob("hourly"));

        var first = await store.TryEnqueueRunAsync(NewRun("nightly"));
        Assert.NotNull(first);
        Assert.True(first!.Id > 0);

        Assert.Null(await store.TryEnqueueRunAsync(NewRun("nightly")));
        Assert.NotNull(await store.TryEnqueueRunAsync(NewRun("hourly")));

        var active = await store.GetActiveRunAsync("nightly");
        Assert.Equal(first.Id, active!.Id);

        // Once the queued run is cancelled the slot frees up.
        var cancelled = await store.CancelRunAsync(first.Id, DateTimeOffset.UnixEpoch.AddMinutes(1));
        Assert.Equal(JobRunStatus.Cancelled, cancelled!.Status);
        Assert.NotNull(cancelled.FinishedAt);
        Assert.NotNull(await store.TryEnqueueRunAsync(NewRun("nightly")));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Cancel_OnARunningRun_StampsTheRequestOnly(string kind)
    {
        using var handle = new StoreHandle(kind);
        var store = handle.Store;
        await store.UpsertAsync(NewJob("nightly"));
        var run = NewRun("nightly");
        run.Status = JobRunStatus.Running;
        run.InstanceId = "i-0123456789abcdef0";
        var inserted = await store.TryEnqueueRunAsync(run);

        var at = DateTimeOffset.UnixEpoch.AddMinutes(5);
        var after = await store.CancelRunAsync(inserted!.Id, at);

        Assert.Equal(JobRunStatus.Running, after!.Status);
        Assert.Equal(at, after.CancelRequestedAt);
        Assert.Null(after.FinishedAt);

        // A second request keeps the first stamp.
        var again = await store.CancelRunAsync(inserted.Id, at.AddMinutes(1));
        Assert.Equal(at, again!.CancelRequestedAt);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Delete_RemovesJobAndScriptHistory_KeepsRuns(string kind)
    {
        using var handle = new StoreHandle(kind);
        var store = handle.Store;
        await store.UpsertAsync(NewJob("nightly"), new JobScriptVersion
        {
            Job = "nightly",
            Version = 1,
            Script = "echo hi",
            Sha256 = JobEndpoints.Sha256Hex("echo hi"),
            UpdatedBy = "seed",
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });
        var run = await store.TryEnqueueRunAsync(NewRun("nightly"));
        await store.CancelRunAsync(run!.Id, DateTimeOffset.UnixEpoch);

        await store.DeleteAsync("nightly");

        Assert.Null(await store.GetAsync("nightly"));
        Assert.Empty(await store.ListScriptVersionsAsync("nightly"));
        Assert.Single(await store.ListRunsAsync("nightly", 50, null));
        Assert.True((await store.GetLastRunsAsync()).ContainsKey("nightly"));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ListRuns_PagesNewestFirst_AndLastRunsPicksTheNewest(string kind)
    {
        using var handle = new StoreHandle(kind);
        var store = handle.Store;
        await store.UpsertAsync(NewJob("nightly"));

        var ids = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            var run = await store.TryEnqueueRunAsync(NewRun("nightly"));
            ids.Add(run!.Id);
            await store.CancelRunAsync(run.Id, DateTimeOffset.UnixEpoch);
        }

        var page = await store.ListRunsAsync("nightly", 2, null);
        Assert.Equal(new[] { ids[3], ids[2] }, page.Select(r => r.Id));

        var next = await store.ListRunsAsync("nightly", 2, page[^1].Id);
        Assert.Equal(new[] { ids[1], ids[0] }, next.Select(r => r.Id));

        Assert.Equal(ids[3], (await store.GetLastRunsAsync())["nightly"].Id);
    }
}
