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

    // ---------- executor primitives (docs/jobs.md section 3) ----------

    private static readonly DateTimeOffset Due = new(2026, 1, 5, 4, 0, 0, TimeSpan.Zero);

    private static JobRun ScheduledRun(string job) => new()
    {
        Job = job,
        Trigger = JobRunTrigger.Schedule,
        Actor = "schedule",
        Status = JobRunStatus.Queued,
        ScriptVersion = 1,
        QueuedAt = Due,
    };

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ListDue_ReturnsEnabledJobsAtOrBeforeNow(string kind)
    {
        using var handle = new StoreHandle(kind);
        var store = handle.Store;
        var due = NewJob("due");
        due.NextRunAt = Due;
        var later = NewJob("later");
        later.NextRunAt = Due.AddMinutes(1);
        var disabled = NewJob("disabled");
        disabled.Enabled = false;
        disabled.NextRunAt = Due.AddDays(-1);
        var manual = NewJob("manual");
        manual.Schedule = null;
        await store.UpsertAsync(due);
        await store.UpsertAsync(later);
        await store.UpsertAsync(disabled);
        await store.UpsertAsync(manual);

        Assert.Equal(new[] { "due" }, (await store.ListDueAsync(Due)).Select(j => j.Name));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ClaimScheduled_WinsOnce_AndAdvancesNextRunAt(string kind)
    {
        using var handle = new StoreHandle(kind);
        var store = handle.Store;
        var job = NewJob("nightly");
        job.NextRunAt = Due;
        await store.UpsertAsync(job);

        var next = Due.AddDays(1);
        var first = await store.TryClaimScheduledRunAsync("nightly", Due, next, ScheduledRun("nightly"));
        var second = await store.TryClaimScheduledRunAsync("nightly", Due, next, ScheduledRun("nightly"));

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(next, (await store.GetAsync("nightly"))!.NextRunAt);
        Assert.Single(await store.ListRunsAsync("nightly", 50, null));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ClaimScheduled_WithAnActiveRun_ChangesNothing(string kind)
    {
        using var handle = new StoreHandle(kind);
        var store = handle.Store;
        var job = NewJob("nightly");
        job.NextRunAt = Due;
        await store.UpsertAsync(job);
        await store.TryEnqueueRunAsync(NewRun("nightly"));

        Assert.Null(await store.TryClaimScheduledRunAsync("nightly", Due, Due.AddDays(1), ScheduledRun("nightly")));
        Assert.Equal(Due, (await store.GetAsync("nightly"))!.NextRunAt);
        Assert.Single(await store.ListRunsAsync("nightly", 50, null));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ClaimQueued_IsWonOnce_AndRecordsTheInstance(string kind)
    {
        using var handle = new StoreHandle(kind);
        var store = handle.Store;
        await store.UpsertAsync(NewJob("nightly"));
        var run = await store.TryEnqueueRunAsync(NewRun("nightly"));

        Assert.True(await store.TryClaimQueuedRunAsync(run!.Id, "i-0000000000000000a", Due));
        Assert.False(await store.TryClaimQueuedRunAsync(run.Id, "i-0000000000000000b", Due));

        var claimed = await store.GetRunAsync(run.Id);
        Assert.Equal(JobRunStatus.Running, claimed!.Status);
        Assert.Equal("i-0000000000000000a", claimed.InstanceId);
        Assert.Equal(Due, claimed.StartedAt);
        Assert.Equal(new[] { run.Id }, (await store.ListRunsByStatusAsync(JobRunStatus.Running)).Select(r => r.Id));
        Assert.Empty(await store.ListRunsByStatusAsync(JobRunStatus.Queued));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Finish_And_Lost_AreConditionalOnTheOwningInstance(string kind)
    {
        using var handle = new StoreHandle(kind);
        var store = handle.Store;
        await store.UpsertAsync(NewJob("nightly"));
        var run = await store.TryEnqueueRunAsync(NewRun("nightly"));
        await store.TryClaimQueuedRunAsync(run!.Id, "i-0000000000000000a", Due);
        await store.SetRunImageDigestAsync(run.Id, "sha256:0123");

        Assert.False(await store.MarkRunLostAsync(run.Id, "i-0000000000000000b", Due, "wrong instance"));
        Assert.True(await store.MarkRunLostAsync(run.Id, "i-0000000000000000a", Due, "no heartbeat"));
        Assert.Equal(JobRunStatus.Lost, (await store.GetRunAsync(run.Id))!.Status);

        // The owning instance's real outcome replaces the lost guess.
        Assert.False(await store.FinishRunAsync(run.Id, "i-0000000000000000b", JobRunStatus.Succeeded, 0, null, Due));
        Assert.True(await store.FinishRunAsync(run.Id, "i-0000000000000000a", JobRunStatus.Failed, 2, "tail", Due.AddMinutes(1)));

        var finished = await store.GetRunAsync(run.Id);
        Assert.Equal(JobRunStatus.Failed, finished!.Status);
        Assert.Equal(2, finished.ExitCode);
        Assert.Equal("tail", finished.Detail);
        Assert.Equal("sha256:0123", finished.ImageDigest);
        Assert.Equal(Due.AddMinutes(1), finished.FinishedAt);

        // A finished run is never finished twice.
        Assert.False(await store.FinishRunAsync(run.Id, "i-0000000000000000a", JobRunStatus.Succeeded, 0, null, Due));
    }
}
