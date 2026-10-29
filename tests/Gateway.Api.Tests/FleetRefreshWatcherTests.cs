using System.Text.Json;
using Gateway.Api.Instances;
using Gateway.Api.Management;
using Gateway.Api.Reconcile;
using Gateway.Api.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Api.Tests;

/// <summary>
/// <see cref="FleetRefreshWatcher"/> (docs/load.md section 9): after a fleet class
/// change raised the group's max by one, every tick publishes the refresh's progress,
/// and the tick that sees it end puts the max back and writes one
/// <c>refresh_ended</c> row, whether the refresh succeeded, failed or was cancelled.
/// Only the fresh leader acts while the reconciler runs.
/// </summary>
public class FleetRefreshWatcherTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static async Task<string> StartClassChangeAsync(ResourceTestHost host)
    {
        var response = await host.Admin().PutAsync("/mgmt/resources/fleet/class", ResourceTestHost.Json("""{"instanceClass":"m7g.large"}"""));
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(new FleetGroupSize(2, 2, 3, 2), host.Scaler.Groups[ResourceTestHost.FleetGroup]);
        return body.GetProperty("refreshId").GetString()!;
    }

    private static IEnumerable<JsonElement> RefreshEvents(ResourceTestHost host) =>
        host.ResourceEvents().Where(e => e.Event == "refresh").Select(e => JsonSerializer.SerializeToElement(e.Data, Web));

    [Theory]
    [InlineData("Successful")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("RollbackSuccessful")]
    public async Task Tick_RestoresTheMax_WhenTheRefreshEnds(string terminal)
    {
        await using var host = new ResourceTestHost();
        var refreshId = await StartClassChangeAsync(host);
        var watcher = host.Watcher();

        // In flight: progress is published, the max stays raised.
        host.Scaler.SetRefresh(ResourceTestHost.FleetGroup, refreshId, "InProgress", 50);
        await watcher.TickAsync();
        Assert.Equal(3, host.Scaler.Groups[ResourceTestHost.FleetGroup].Max);
        var progress = RefreshEvents(host).Last();
        Assert.Equal(refreshId, progress.GetProperty("refreshId").GetString());
        Assert.Equal("InProgress", progress.GetProperty("status").GetString());
        Assert.Equal(50, progress.GetProperty("percentComplete").GetInt32());

        // Ended: the max goes back to 2 and one refresh_ended row is written.
        host.Scaler.SetRefresh(ResourceTestHost.FleetGroup, refreshId, terminal, 100);
        await watcher.TickAsync();
        Assert.Equal(new FleetGroupSize(2, 2, 2, 2), host.Scaler.Groups[ResourceTestHost.FleetGroup]);
        Assert.Equal(terminal, RefreshEvents(host).Last().GetProperty("status").GetString());

        var ended = Assert.Single(await host.AuditAsync(), r => r.Action == ResourceEndpoints.RefreshEndedAction);
        Assert.Equal((FleetRefreshWatcher.Actor, "fleet", "max 3", "max 2"), (ended.Actor, ended.Resource, ended.Before, ended.After));
        var detail = JsonDocument.Parse(ended.Detail!).RootElement;
        Assert.Equal(refreshId, detail.GetProperty("refreshId").GetString());
        Assert.Equal(terminal, detail.GetProperty("status").GetString());

        // A later tick has nothing left to follow.
        var calls = host.Scaler.CallLog.Count;
        var events = host.ResourceEvents().Count;
        await watcher.TickAsync();
        Assert.Equal(calls, host.Scaler.CallLog.Count);
        Assert.Equal(events, host.ResourceEvents().Count);
        Assert.Single(await host.AuditAsync(), r => r.Action == ResourceEndpoints.RefreshEndedAction);
    }

    [Fact]
    public async Task Tick_RestoresTheMax_WhenAutoScalingNoLongerKnowsTheRefresh()
    {
        await using var host = new ResourceTestHost();
        await StartClassChangeAsync(host);
        host.Scaler.Refreshes[ResourceTestHost.FleetGroup].Clear();

        await host.Watcher().TickAsync();

        Assert.Equal(2, host.Scaler.Groups[ResourceTestHost.FleetGroup].Max);
        Assert.Equal("NotFound", RefreshEvents(host).Last().GetProperty("status").GetString());
    }

    [Fact]
    public async Task Tick_FollowsARollbackToo_AndIgnoresRefreshesItDidNotStart()
    {
        await using var host = new ResourceTestHost();

        // A refresh the CI deploy started is not the watcher's to restore.
        host.Scaler.SetRefresh(ResourceTestHost.FleetGroup, "refresh-placeholder-ci", "Successful", 100);
        await host.Watcher().TickAsync();
        Assert.DoesNotContain(host.Scaler.CallLog, c => c.StartsWith("describe-refreshes", StringComparison.Ordinal));

        var response = await host.Admin().PutAsync("/mgmt/resources/fleet/class/rollback", ResourceTestHost.Json("""{"templateVersion":4}"""));
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(3, host.Scaler.Groups[ResourceTestHost.FleetGroup].Max);

        host.Scaler.SetRefresh(ResourceTestHost.FleetGroup, "refresh-placeholder-1", "Successful", 100);
        await host.Watcher().TickAsync();
        Assert.Equal(2, host.Scaler.Groups[ResourceTestHost.FleetGroup].Max);
    }

    [Fact]
    public async Task Tick_ActsOnlyAsTheFreshLeader_WhileTheReconcilerRuns()
    {
        await using var host = new ResourceTestHost();
        var refreshId = await StartClassChangeAsync(host);
        host.Scaler.SetRefresh(ResourceTestHost.FleetGroup, refreshId, "Successful", 100);

        var leadership = new LeadershipState();
        var watcher = new FleetRefreshWatcher(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            leadership,
            new ReconcilerOptions { Enabled = true },
            host.Events,
            TimeProvider.System,
            NullLogger<FleetRefreshWatcher>.Instance,
            FleetRefreshWatcher.PollInterval);

        await watcher.TickAsync();
        Assert.Equal(3, host.Scaler.Groups[ResourceTestHost.FleetGroup].Max);

        leadership.Update(false, "i-0placeholderother", DateTimeOffset.UtcNow);
        await watcher.TickAsync();
        Assert.Equal(3, host.Scaler.Groups[ResourceTestHost.FleetGroup].Max);

        leadership.Update(true, "i-0placeholderself", DateTimeOffset.UtcNow);
        await watcher.TickAsync();
        Assert.Equal(2, host.Scaler.Groups[ResourceTestHost.FleetGroup].Max);
    }

    [Fact]
    public async Task Tick_NeverRestoresBelowDesired()
    {
        await using var host = new ResourceTestHost();
        var refreshId = await StartClassChangeAsync(host);

        // Someone outside the gateway raised desired during the refresh.
        host.Scaler.Groups[ResourceTestHost.FleetGroup] = new FleetGroupSize(2, 3, 3, 3);
        host.Scaler.SetRefresh(ResourceTestHost.FleetGroup, refreshId, "Successful", 100);
        await host.Watcher().TickAsync();

        Assert.Equal(new FleetGroupSize(2, 3, 3, 3), host.Scaler.Groups[ResourceTestHost.FleetGroup]);
        Assert.Single(await host.AuditAsync(), r => r.Action == ResourceEndpoints.RefreshEndedAction);
    }
}
