using Gateway.Api.Instances;
using Gateway.Api.RealTime;

namespace Gateway.Api.Tests;

/// <summary>
/// <see cref="LinuxNodeStats"/> against fixture <c>/proc</c> files (docs/load.md
/// section 4): cpu from two <c>/proc/stat</c> readings, memory as total minus
/// available of total, disk from the root filesystem, hub connections from the
/// membership registry, and a failed read leaving only that value null.
/// </summary>
public sealed class LinuxNodeStatsTests : IDisposable
{
    private readonly string _proc = Directory.CreateTempSubdirectory("nodestats-").FullName;

    public void Dispose() => Directory.Delete(_proc, recursive: true);

    private void WriteStat(ulong user, ulong system, ulong idle, ulong iowait) =>
        File.WriteAllText(Path.Combine(_proc, "stat"),
            $"cpu  {user} 0 {system} {idle} {iowait} 0 0 0 0 0\ncpu0 1 0 1 1 0 0 0 0 0 0\nintr 0\n");

    private void WriteMeminfo(ulong totalKb, ulong availableKb) =>
        File.WriteAllText(Path.Combine(_proc, "meminfo"),
            $"MemTotal:       {totalKb} kB\nMemFree:         1000 kB\nMemAvailable:   {availableKb} kB\nBuffers:            0 kB\n");

    [Fact]
    public void Cpu_IsNullOnTheFirstRead_ThenTheBusyShareSinceThePrevious()
    {
        WriteStat(user: 100, system: 100, idle: 700, iowait: 100);
        WriteMeminfo(1000, 250);
        var stats = new LinuxNodeStats(null, procRoot: _proc, diskRoot: _proc);

        Assert.Null(stats.Read().Cpu);

        // +300 busy (user 200, system 100), +100 idle, +0 iowait: 75% busy.
        WriteStat(user: 300, system: 200, idle: 800, iowait: 100);
        Assert.Equal(75.0, stats.Read().Cpu);

        // No time passed: no figure rather than a division by zero.
        Assert.Null(stats.Read().Cpu);
    }

    [Fact]
    public void Memory_IsTotalMinusAvailable_OfTotal_AndDiskIsAPercent()
    {
        WriteStat(1, 1, 1, 1);
        WriteMeminfo(totalKb: 2_000_000, availableKb: 500_000);
        var stats = new LinuxNodeStats(null, procRoot: _proc, diskRoot: Path.GetPathRoot(_proc)!);

        var reading = stats.Read();

        Assert.Equal(75.0, reading.Memory);
        Assert.NotNull(reading.Disk);
        Assert.InRange(reading.Disk!.Value, 0, 100);
        Assert.Null(reading.HubConnections);
    }

    [Fact]
    public void HubConnections_ComeFromTheMembershipRegistry()
    {
        WriteStat(1, 1, 1, 1);
        WriteMeminfo(1000, 500);
        var membership = new HubChannelMembership();
        membership.Connect("c1");
        membership.Connect("c2");
        var stats = new LinuxNodeStats(membership, procRoot: _proc, diskRoot: _proc);

        Assert.Equal(2, stats.Read().HubConnections);
    }

    [Fact]
    public void AFailedRead_LeavesOnlyThatValueNull_AndNeverThrows()
    {
        WriteStat(1, 1, 1, 1);
        File.WriteAllText(Path.Combine(_proc, "meminfo"), "MemTotal: 1000 kB\n");
        var stats = new LinuxNodeStats(new HubChannelMembership(), procRoot: _proc, diskRoot: _proc);

        var reading = stats.Read();
        Assert.Null(reading.Memory);
        Assert.Equal(0, reading.HubConnections);

        File.Delete(Path.Combine(_proc, "stat"));
        Assert.Null(stats.Read().Cpu);
        Assert.Null(stats.Read().Memory);
    }

    [Fact]
    public void ParseCpuLine_SumsTheFirstEightCounters_IdleIsIdlePlusIowait()
    {
        var (total, idle) = LinuxNodeStats.ParseCpuLine("cpu  10 20 30 40 50 60 70 80 90 100");

        Assert.Equal(360UL, total);
        Assert.Equal(90UL, idle);
    }

    [Fact]
    public void NullNodeStats_ReadsEveryValueNull()
    {
        Assert.Equal(NodeStatsSnapshot.Empty, new NullNodeStats().Read());
    }
}
