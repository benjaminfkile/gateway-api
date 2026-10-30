using Gateway.Api.Data;
using Gateway.Api.Load;
using Gateway.Api.Management;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Gateway.Api.Tests;

/// <summary>
/// The <see cref="ILoadStore"/> contract, run against both the Sqlite-backed
/// <see cref="EfLoadStore"/> and <see cref="FakeLoadStore"/> so the fake the endpoint
/// tests rely on behaves like the real store: one non-terminal run, the conditional
/// claim and check-in, stop, verdict and delete rules, stats reads and peaks, and
/// script history that outlives a deleted scenario.
/// </summary>
public class LoadStoreTests
{
    public static TheoryData<string> Stores => new() { "ef", "fake" };

    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 2, 0, 0, TimeSpan.Zero);

    private sealed class StoreHandle : IDisposable
    {
        private readonly SqliteConnection? _connection;
        private readonly GatewayDbContext? _db;
        private readonly FakeLoadStore? _fake;

        public StoreHandle(string kind)
        {
            if (kind == "fake")
            {
                _fake = new FakeLoadStore();
                Store = _fake;
                return;
            }

            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            _db = NewContext(_connection);
            _db.Database.EnsureCreated();
            Store = new EfLoadStore(_db);
        }

        public ILoadStore Store { get; }

        /// <summary>Insert a run directly, bypassing the one-non-terminal rule.</summary>
        public void Insert(LoadRun run)
        {
            if (_fake is not null)
            {
                _fake.Seed(run);
                return;
            }

            _db!.LoadRuns.Add(run);
            _db.SaveChanges();
            _db.Entry(run).State = EntityState.Detached;
        }

        public void Dispose()
        {
            _db?.Dispose();
            _connection?.Dispose();
        }
    }

    private static GatewayDbContext NewContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options);

    private static LoadRun NewRun(string status = LoadRunStatus.Queued, int generators = 2, string scenario = "tracker") =>
        LoadTestHost.Run(0, status, scenario: scenario, generators: generators);

    // ---------- runs ----------

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task TryCreateRun_AdmitsOneNonTerminalRun(string kind)
    {
        using var handle = new StoreHandle(kind);
        var store = handle.Store;

        var first = await store.TryCreateRunAsync(NewRun());
        Assert.NotNull(first);
        Assert.True(first!.Id > 0);
        Assert.Null(await store.TryCreateRunAsync(NewRun(scenario: "other")));
        Assert.Equal(first.Id, (await store.GetActiveRunAsync())?.Id);
        Assert.Equal(first.Id, (await store.GetActiveRunForScenarioAsync("tracker"))?.Id);
        Assert.Null(await store.GetActiveRunForScenarioAsync("other"));

        handle.Insert(NewRun(LoadRunStatus.Failed));
        Assert.Equal(2, (await store.ListRunsAsync(10, null)).Count);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task TryCreateRun_AdmitsANewRunOnceTheActiveOneIsTerminal(string kind)
    {
        using var handle = new StoreHandle(kind);
        handle.Insert(NewRun(LoadRunStatus.Stopped));
        handle.Insert(NewRun(LoadRunStatus.Lost));

        Assert.Null(await handle.Store.GetActiveRunAsync());
        Assert.NotNull(await handle.Store.TryCreateRunAsync(NewRun()));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ListRuns_NewestFirst_WithKeysetPaging(string kind)
    {
        using var handle = new StoreHandle(kind);
        for (var i = 0; i < 4; i++)
        {
            handle.Insert(NewRun(LoadRunStatus.Succeeded));
        }

        var all = await handle.Store.ListRunsAsync(10, null);
        Assert.Equal(all.Select(r => r.Id).OrderByDescending(id => id), all.Select(r => r.Id));
        var page = await handle.Store.ListRunsAsync(2, all[0].Id);
        Assert.Equal(all.Skip(1).Take(2).Select(r => r.Id), page.Select(r => r.Id));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Claim_MovesQueuedToGeneratorsUp_Once(string kind)
    {
        using var handle = new StoreHandle(kind);
        var store = handle.Store;
        var run = await store.TryCreateRunAsync(NewRun());

        var claimed = await store.ClaimQueuedRunAsync(run!.Id, "leader", T0);
        Assert.NotNull(claimed);
        Assert.Equal(LoadRunStatus.GeneratorsUp, claimed!.Status);
        Assert.Equal(T0, claimed.StartedAt);
        Assert.Equal(LoadRunStatus.GeneratorsUp, LoadJson.ParseTimeline(claimed.Timeline).Last().Status);
        Assert.Null(await store.ClaimQueuedRunAsync(run.Id, "leader", T0));

        Assert.Null(await store.ClaimQueuedRunAsync(999, "leader", T0));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Claim_RefusedWhileAnotherRunIsNonTerminal(string kind)
    {
        using var handle = new StoreHandle(kind);
        handle.Insert(NewRun(LoadRunStatus.Draining));
        handle.Insert(NewRun());
        var queued = (await handle.Store.ListRunsAsync(1, null))[0];

        Assert.Null(await handle.Store.ClaimQueuedRunAsync(queued.Id, "leader", T0));
        Assert.Equal(LoadRunStatus.Queued, (await handle.Store.GetRunAsync(queued.Id))?.Status);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task CheckIn_AssignsInOrder_RepeatsKeepTheirIndex_ThenFull(string kind)
    {
        using var handle = new StoreHandle(kind);
        var store = handle.Store;
        handle.Insert(NewRun(LoadRunStatus.GeneratorsUp));
        var id = (await store.ListRunsAsync(1, null))[0].Id;

        var a = await store.CheckInAsync(id, "i-0a");
        var b = await store.CheckInAsync(id, "i-0b");
        var again = await store.CheckInAsync(id, "i-0a");
        var c = await store.CheckInAsync(id, "i-0c");

        Assert.Equal((LoadCheckInOutcome.Assigned, 0), (a.Outcome, a.Index));
        Assert.Equal((LoadCheckInOutcome.Assigned, 1), (b.Outcome, b.Index));
        Assert.Equal((LoadCheckInOutcome.Assigned, 0), (again.Outcome, again.Index));
        Assert.Equal(LoadCheckInOutcome.Full, c.Outcome);

        var run = await store.GetRunAsync(id);
        Assert.Equal(2, run!.CheckedIn);
        Assert.Equal(new Dictionary<string, int> { ["i-0a"] = 0, ["i-0b"] = 1 }, LoadJson.ParseCheckins(run.Checkins));
        Assert.Equal(LoadCheckInOutcome.NotFound, (await store.CheckInAsync(999, "i-0a")).Outcome);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task CheckIn_NotAcceptingOutsideGeneratorsUp(string kind)
    {
        using var handle = new StoreHandle(kind);
        handle.Insert(NewRun(LoadRunStatus.Holding));
        var id = (await handle.Store.ListRunsAsync(1, null))[0].Id;

        var result = await handle.Store.CheckInAsync(id, "i-0a");

        Assert.Equal(LoadCheckInOutcome.NotAccepting, result.Outcome);
        Assert.Equal(0, (await handle.Store.GetRunAsync(id))!.CheckedIn);
    }

    [Fact]
    public async Task EfCheckIn_TwoStoresRacing_NeverShareAnIndex()
    {
        // Two contexts on two connections to one file database: each check-in reads,
        // then updates conditionally on the value it read, so the loser re-reads.
        var path = Path.Combine(Path.GetTempPath(), $"load-checkin-{Guid.NewGuid():N}.db");
        try
        {
            using (var setup = new SqliteConnection($"DataSource={path}"))
            {
                setup.Open();
                using var db = NewContext(setup);
                db.Database.EnsureCreated();
                db.LoadRuns.Add(NewRun(LoadRunStatus.GeneratorsUp, generators: 8));
                db.SaveChanges();
            }

            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
            {
                await using var connection = new SqliteConnection($"DataSource={path};Pooling=False;Default Timeout=30");
                await connection.OpenAsync();
                await using var db = NewContext(connection);
                return await new EfLoadStore(db).CheckInAsync(1, $"i-0{i}");
            })));

            Assert.All(results, r => Assert.Equal(LoadCheckInOutcome.Assigned, r.Outcome));
            Assert.Equal(Enumerable.Range(0, 8), results.Select(r => r.Index).OrderBy(i => i));

            using var check = new SqliteConnection($"DataSource={path};Pooling=False");
            check.Open();
            using var verify = NewContext(check);
            var run = verify.LoadRuns.Single();
            Assert.Equal(8, run.CheckedIn);
            Assert.Equal(8, LoadJson.ParseCheckins(run.Checkins).Values.Distinct().Count());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task RequestStop_StampsOnce_AndLeavesTerminalRunsAlone(string kind)
    {
        using var handle = new StoreHandle(kind);
        var store = handle.Store;
        handle.Insert(NewRun(LoadRunStatus.Succeeded));
        var done = (await store.ListRunsAsync(1, null))[0];
        var active = await store.TryCreateRunAsync(NewRun());

        var first = await store.RequestStopAsync(active!.Id, "alice", T0);
        var second = await store.RequestStopAsync(active.Id, "bob", T0.AddMinutes(1));
        Assert.Equal(T0, first!.StopRequestedAt);
        Assert.Equal(T0, second!.StopRequestedAt);
        Assert.Equal("alice", Assert.Single(LoadJson.ParseTimeline(second.Timeline)).By);

        Assert.Null((await store.RequestStopAsync(done.Id, "alice", T0))!.StopRequestedAt);
        Assert.Null(await store.RequestStopAsync(999, "alice", T0));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Verdict_AndDelete_OnlyOnTerminalRuns_DeleteTakesTheStats(string kind)
    {
        using var handle = new StoreHandle(kind);
        var store = handle.Store;
        handle.Insert(NewRun(LoadRunStatus.Succeeded));
        var done = (await store.ListRunsAsync(1, null))[0];
        var active = await store.TryCreateRunAsync(NewRun());
        await store.AppendStatAsync(new LoadRunStat { RunId = done.Id, Source = LoadStatSource.Generator, Index = 0, At = T0, Body = "{}" });
        await store.AppendStatAsync(new LoadRunStat { RunId = active!.Id, Source = LoadStatSource.Generator, Index = 0, At = T0, Body = "{}" });

        Assert.True(await store.SetVerdictAsync(done.Id, "fine"));
        Assert.Equal("fine", (await store.GetRunAsync(done.Id))!.Verdict);
        Assert.False(await store.SetVerdictAsync(active.Id, "early"));

        Assert.False(await store.DeleteRunAsync(active.Id));
        Assert.True(await store.DeleteRunAsync(done.Id));
        Assert.Null(await store.GetRunAsync(done.Id));
        Assert.Empty(await store.ListStatsAsync(done.Id, null, null, null, null));
        Assert.Single(await store.ListStatsAsync(active.Id, null, null, null, null));
        Assert.False(await store.DeleteRunAsync(done.Id));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task TryAdvance_WritesTheSchedulerColumns_OnlyWhileStatusAndTimelineAreUnchanged(string kind)
    {
        using var handle = new StoreHandle(kind);
        var store = handle.Store;
        var created = await store.TryCreateRunAsync(NewRun());
        var run = await store.ClaimQueuedRunAsync(created!.Id, "leader", T0);
        await store.CheckInAsync(run!.Id, "i-0placeholder0");

        var next = run.Clone();
        next.Status = LoadRunStatus.Ramping;
        next.RampStartedAt = T0.AddMinutes(1);
        next.SpecToken = null;
        next.Detail = "note";
        next.Timeline = LoadJson.AppendTimeline(run.Timeline, T0.AddMinutes(1), LoadRunStatus.Ramping, "leader");

        var after = await store.TryAdvanceRunAsync(run, next);
        Assert.NotNull(after);
        Assert.Equal((LoadRunStatus.Ramping, T0.AddMinutes(1), "note"), (after!.Status, after.RampStartedAt, after.Detail));
        // Check-in columns are never written by an advance.
        Assert.Equal(1, after.CheckedIn);

        // A step decided on the old read matches nothing.
        Assert.Null(await store.TryAdvanceRunAsync(run, next));

        // A stop request changes the timeline, so an advance read before it loses.
        var stopped = await store.RequestStopAsync(run.Id, "alice-admin", T0.AddMinutes(2));
        var late = after.Clone();
        late.Status = LoadRunStatus.Holding;
        Assert.Null(await store.TryAdvanceRunAsync(after, late));
        Assert.Equal(LoadRunStatus.Ramping, (await store.GetRunAsync(run.Id))!.Status);
        Assert.NotNull(await store.TryAdvanceRunAsync(stopped!, late));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task SetScenarioValidation_OnlyForTheVersionTheRunRan(string kind)
    {
        using var handle = new StoreHandle(kind);
        var store = handle.Store;
        await store.UpsertScenarioAsync(LoadTestHost.Scenario("tracker"));

        Assert.False(await store.SetScenarioValidationAsync("tracker", 2, 7));
        Assert.Null((await store.GetScenarioAsync("tracker"))!.LastValidationRunId);
        Assert.True(await store.SetScenarioValidationAsync("tracker", 1, 7));
        Assert.Equal(7, (await store.GetScenarioAsync("tracker"))!.LastValidationRunId);
        Assert.False(await store.SetScenarioValidationAsync("missing", 1, 7));
    }

    // ---------- stats ----------

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Stats_ListedByTime_Filtered_AndTheNewestLinePerGenerator(string kind)
    {
        using var handle = new StoreHandle(kind);
        var store = handle.Store;
        var run = await store.TryCreateRunAsync(NewRun());

        async Task<LoadRunStat> AddAsync(string source, int? index, int second) =>
            await store.AppendStatAsync(new LoadRunStat
            {
                RunId = run!.Id,
                Source = source,
                Index = index,
                At = T0.AddSeconds(second),
                Body = $"{{\"second\":{second}}}",
            });

        var late = await AddAsync(LoadStatSource.Generator, 0, 10);
        var early = await AddAsync(LoadStatSource.Generator, 0, 0);
        var other = await AddAsync(LoadStatSource.Generator, 1, 5);
        var fleet = await AddAsync(LoadStatSource.Fleet, null, 7);

        var all = await store.ListStatsAsync(run!.Id, null, null, null, null);
        Assert.Equal(new[] { early.Id, other.Id, fleet.Id, late.Id }, all.Select(s => s.Id));

        Assert.Equal(new[] { fleet.Id }, (await store.ListStatsAsync(run.Id, null, null, LoadStatSource.Fleet, null)).Select(s => s.Id));
        Assert.Equal(new[] { early.Id, late.Id }, (await store.ListStatsAsync(run.Id, null, null, null, 0)).Select(s => s.Id));
        Assert.Equal(new[] { other.Id, fleet.Id },
            (await store.ListStatsAsync(run.Id, T0.AddSeconds(5), T0.AddSeconds(7), null, null)).Select(s => s.Id));

        // Newest received per index: index 0's last insert is the second-0 line.
        var latest = await store.GetLatestGeneratorStatsAsync(run.Id);
        Assert.Equal(new[] { early.Id, other.Id }, latest.Select(s => s.Id));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task UpdatePeaks_KeepsTheMaximumOfEveryKey(string kind)
    {
        using var handle = new StoreHandle(kind);
        var store = handle.Store;
        var run = await store.TryCreateRunAsync(NewRun());

        await store.UpdatePeaksAsync(run!.Id, new Dictionary<string, double> { ["lag.p(99)"] = 310, ["vus.value"] = 480 });
        await store.UpdatePeaksAsync(run.Id, new Dictionary<string, double> { ["lag.p(99)"] = 290, ["errors.count"] = 2 });
        await store.UpdatePeaksAsync(999, new Dictionary<string, double> { ["x"] = 1 });

        var peaks = LoadJson.ParsePeaks((await store.GetRunAsync(run.Id))!.Peaks);
        Assert.Equal(new Dictionary<string, double> { ["errors.count"] = 2, ["lag.p(99)"] = 310, ["vus.value"] = 480 }, peaks);
    }

    // ---------- scenarios ----------

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Scenarios_UpsertListDelete_HistoryOutlivesTheScenario(string kind)
    {
        using var handle = new StoreHandle(kind);
        var store = handle.Store;
        await store.UpsertScenarioAsync(LoadTestHost.Scenario("tracker"), LoadTestHost.Version("tracker", 1, "v1"));
        await store.UpsertScenarioAsync(LoadTestHost.Scenario("alpha"), LoadTestHost.Version("alpha", 1, "a1"));

        var updated = LoadTestHost.Scenario("tracker", script: "v2");
        updated.ScriptVersion = 2;
        updated.LastValidationRunId = 5;
        await store.UpsertScenarioAsync(updated, LoadTestHost.Version("tracker", 2, "v2"));

        Assert.Equal(new[] { "alpha", "tracker" }, (await store.ListScenariosAsync()).Select(s => s.Name));
        var tracker = await store.GetScenarioAsync("tracker");
        Assert.Equal((2, "v2", 5L), (tracker!.ScriptVersion, tracker.Script, tracker.LastValidationRunId!.Value));
        Assert.Equal(new[] { 2, 1 }, (await store.ListScenarioVersionsAsync("tracker")).Select(v => v.Version));
        Assert.Equal("v1", (await store.GetScenarioVersionAsync("tracker", 1))?.Script);

        await store.DeleteScenarioAsync("tracker");
        Assert.Null(await store.GetScenarioAsync("tracker"));
        Assert.Equal(2, (await store.ListScenarioVersionsAsync("tracker")).Count);
    }

    [Fact]
    public async Task NullStore_AnswersEmpty_AndMatchesNothing()
    {
        var store = new NullLoadStore();

        Assert.Empty(await store.ListScenariosAsync());
        Assert.Empty(await store.ListRunsAsync(10, null));
        Assert.Null(await store.GetRunAsync(1));
        Assert.Null(await store.ClaimQueuedRunAsync(1, "leader", T0));
        Assert.Equal(LoadCheckInOutcome.NotFound, (await store.CheckInAsync(1, "i-0a")).Outcome);
        Assert.False(await store.SetVerdictAsync(1, "x"));
        Assert.False(await store.DeleteRunAsync(1));
        Assert.Empty(await store.ListStatsAsync(1, null, null, null, null));
    }
}
