using Gateway.Api.Data;
using Gateway.Api.Load;
using Gateway.Api.Resources;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Api.Tests;

/// <summary>
/// The load data model and its wiring (docs/load.md sections 2 and 12): the AddLoad
/// migration and model snapshot agree with the DbContext, the migration applies and
/// the tables are usable, the options bind with their defaults, the null fleet scaler
/// refuses everything, and the host registers the part 1 seams.
/// </summary>
public class LoadModelTests
{
    [Fact]
    public void ModelSnapshot_MatchesTheModel_NoPendingChanges()
    {
        // The design-time factory builds the Npgsql model without opening a connection.
        using var db = new GatewayDbContextFactory().CreateDbContext(Array.Empty<string>());

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains(db.Database.GetMigrations(), m => m.EndsWith("_AddLoad", StringComparison.Ordinal));
    }

    [Fact]
    public void ModelSnapshot_CarriesTheLoadTables_TheStatIndex_AndTheCheckinsDefault()
    {
        using var db = new GatewayDbContextFactory().CreateDbContext(Array.Empty<string>());
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;

        var tables = snapshot.GetEntityTypes().Select(e => e.GetTableName()).ToList();
        Assert.Contains("load_scenario", tables);
        Assert.Contains("load_scenario_version", tables);
        Assert.Contains("load_run", tables);
        Assert.Contains("load_run_stat", tables);

        var stat = snapshot.FindEntityType(typeof(LoadRunStat).FullName!)!;
        Assert.Contains(stat.GetIndexes(), i => i.Properties.Select(p => p.GetColumnName()).SequenceEqual(new[] { "run_id", "at" }));

        var checkins = snapshot.FindEntityType(typeof(LoadRun).FullName!)!.FindProperty(nameof(LoadRun.Checkins))!;
        Assert.Equal("checkins", checkins.GetColumnName());
        Assert.Equal("{}", checkins.GetDefaultValue());
        Assert.False(checkins.IsNullable);
    }

    [Fact]
    public void Migration_Applies_OnSqlite_AndTheLoadTablesAreUsable()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var db = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options);

        db.Database.Migrate();

        db.LoadScenarios.Add(LoadTestHost.Scenario("tracker"));
        db.LoadScenarioVersions.Add(LoadTestHost.Version("tracker", 1, "export default function () {}"));
        // The replayed Npgsql migration types the ids bigint, which Sqlite does not
        // auto-number, so the rows carry explicit ids.
        var run = LoadTestHost.Run(1, LoadRunStatus.Queued);
        db.LoadRuns.Add(run);
        db.SaveChanges();
        db.LoadRunStats.Add(new LoadRunStat { Id = 1, RunId = run.Id, Source = LoadStatSource.Generator, Index = 0, At = DateTimeOffset.UnixEpoch, Body = "{}" });
        db.SaveChanges();

        Assert.Equal("{}", db.LoadRuns.AsNoTracking().Single().Checkins);
        Assert.Equal(1, db.LoadRunStats.Count());
    }

    [Fact]
    public void LoadRunStatus_TerminalAndNonTerminal_PartitionEveryStatus()
    {
        Assert.Equal(11, LoadRunStatus.Terminal.Length + LoadRunStatus.NonTerminal.Length);
        Assert.All(LoadRunStatus.Terminal, s => Assert.True(LoadRunStatus.IsTerminal(s)));
        Assert.All(LoadRunStatus.NonTerminal, s => Assert.False(LoadRunStatus.IsTerminal(s)));
        Assert.Empty(LoadRunStatus.Terminal.Intersect(LoadRunStatus.NonTerminal));
    }

    [Fact]
    public void Options_Defaults_MatchTheDesign()
    {
        var options = LoadOptions.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.Null(options.GeneratorGroup);
        Assert.False(options.IsConfigured);
        Assert.Null(options.GeneratorSpecSecret);
        Assert.Equal(2, options.MaxGenerators);
        Assert.Equal(TimeSpan.FromMinutes(5), options.CheckInTimeout);
        Assert.Equal(TimeSpan.FromMinutes(1), options.StatsStaleAfter);
        Assert.Equal(300, options.ValidationHoldSeconds);
        Assert.Equal(20, options.DbConnectionsPerNode);
        Assert.Equal(10, options.DbConnectionMargin);
        Assert.Equal(25, options.DbDowngradeMarginPercent);
        Assert.Equal(TimeSpan.FromSeconds(10), options.FleetSampleInterval);
        Assert.Equal("gateway-job-runner", options.DefaultImage);
        Assert.Equal("latest", options.DefaultTag);
    }

    [Fact]
    public void Options_BindFromTheLoadSection_ImageAndTagFallBackToTheJobDefaults()
    {
        var options = LoadOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Load:GeneratorGroup"] = "load-generators-placeholder",
            ["Load:GeneratorSpecSecret"] = "load-spec-placeholder",
            ["Load:MaxGenerators"] = "4",
            ["Load:CheckInTimeout"] = "00:02:00",
            ["Load:StatsStaleAfter"] = "00:00:30",
            ["Load:ValidationHoldSeconds"] = "120",
            ["Load:DbConnectionsPerNode"] = "30",
            ["Load:DbConnectionMargin"] = "5",
            ["Load:DbDowngradeMarginPercent"] = "40",
            ["Load:FleetSampleInterval"] = "00:00:20",
            ["Jobs:DefaultImage"] = "registry.example.com/runner",
            ["Jobs:DefaultTag"] = "v3",
        }).Build());

        Assert.True(options.IsConfigured);
        Assert.Equal("load-generators-placeholder", options.GeneratorGroup);
        Assert.Equal("load-spec-placeholder", options.GeneratorSpecSecret);
        Assert.Equal(4, options.MaxGenerators);
        Assert.Equal(TimeSpan.FromMinutes(2), options.CheckInTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.StatsStaleAfter);
        Assert.Equal(120, options.ValidationHoldSeconds);
        Assert.Equal(30, options.DbConnectionsPerNode);
        Assert.Equal(5, options.DbConnectionMargin);
        Assert.Equal(40, options.DbDowngradeMarginPercent);
        Assert.Equal(TimeSpan.FromSeconds(20), options.FleetSampleInterval);
        Assert.Equal("registry.example.com/runner", options.DefaultImage);
        Assert.Equal("v3", options.DefaultTag);

        var own = LoadOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Load:DefaultImage"] = "registry.example.com/load-runner",
            ["Load:DefaultTag"] = "k6",
            ["Jobs:DefaultImage"] = "registry.example.com/runner",
        }).Build());
        Assert.Equal(("registry.example.com/load-runner", "k6"), (own.DefaultImage, own.DefaultTag));
    }

    [Fact]
    public async Task NullFleetScaler_RefusesEveryCall()
    {
        var scaler = new NullFleetScaler();
        var calls = new Func<Task>[]
        {
            () => scaler.DescribeGroupAsync("g"),
            () => scaler.SetGroupSizeAsync("g", 0, 0, 0),
            () => scaler.SetDesiredAsync("g", 0),
            () => scaler.DescribeLaunchTemplateAsync("t"),
            () => scaler.CreateLaunchTemplateVersionAsync("t", "m7g.large"),
            () => scaler.SetDefaultLaunchTemplateVersionAsync("t", 2),
            () => scaler.StartInstanceRefreshAsync("g", new FleetRefreshPreferences(100, 150, 300)),
            () => scaler.CancelInstanceRefreshAsync("g"),
            () => scaler.DescribeInstanceRefreshesAsync("g"),
        };

        foreach (var call in calls)
        {
            var ex = await Assert.ThrowsAsync<NotSupportedException>(call);
            Assert.Equal(NullFleetScaler.NotConfiguredMessage, ex.Message);
        }
    }

    [Fact]
    public async Task EfDatabaseHeadroom_AnswersNull_OffPostgres()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var db = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>().UseSqlite(connection).Options);

        Assert.Null(await new EfDatabaseHeadroom(db).GetAsync());
        Assert.Null(await new NullDatabaseHeadroom().GetAsync());
    }

    [Fact]
    public async Task Host_RegistersTheNullScaler_AndEveryPreflightCheckInOrder()
    {
        await using var factory = new ManagementApiFactory();
        using var scope = factory.Services.CreateScope();

        Assert.IsType<NullFleetScaler>(scope.ServiceProvider.GetRequiredService<IFleetScaler>());
        Assert.IsType<LoadPreflight>(scope.ServiceProvider.GetRequiredService<ILoadPreflight>());
        Assert.Equal(
            new[]
            {
                "generator_group", "fleet_healthy", "target_known", "scenario_validated",
                "presence_off", "database_headroom", "fleet_class", "target_confirmed",
            },
            scope.ServiceProvider.GetServices<ILoadPreflightCheck>().Select(c => c.Name));
    }
}
