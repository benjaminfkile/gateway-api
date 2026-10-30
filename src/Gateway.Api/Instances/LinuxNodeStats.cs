using System.Globalization;
using Gateway.Api.RealTime;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Instances;

/// <summary>
/// <see cref="INodeStats"/> on Linux (docs/load.md section 4). Cpu is the busy share
/// of the whole box between this reading and the previous one, from the aggregate
/// <c>cpu</c> line of <c>/proc/stat</c> (the previous counters are kept in memory, so
/// the first reading has no cpu value). Memory is used of total from
/// <c>/proc/meminfo</c>, used being <c>MemTotal</c> minus <c>MemAvailable</c>. Disk
/// is the root filesystem's used share from <see cref="DriveInfo"/>. Hub connections
/// is the count the hub's own <see cref="HubChannelMembership"/> registry holds.
/// <para>
/// A read that fails leaves that value null and logs a warning once per value, so a
/// box without one of the sources does not flood the log every heartbeat. The paths
/// are constructor arguments so tests can point them at fixture files.
/// </para>
/// </summary>
public sealed class LinuxNodeStats : INodeStats
{
    private readonly HubChannelMembership? _membership;
    private readonly ILogger? _logger;
    private readonly string _procStat;
    private readonly string _procMeminfo;
    private readonly string _diskRoot;

    // Previous /proc/stat counters (total, idle), so cpu is the delta since the last beat.
    private readonly object _cpuLock = new();
    private (ulong Total, ulong Idle)? _previousCpu;

    // One flag per value: a failing read logs once, not every heartbeat.
    private int _cpuLogged;
    private int _memoryLogged;
    private int _diskLogged;

    public LinuxNodeStats(
        HubChannelMembership? membership,
        ILogger<LinuxNodeStats>? logger = null,
        string procRoot = "/proc",
        string diskRoot = "/")
    {
        _membership = membership;
        _logger = logger;
        _procStat = Path.Combine(procRoot, "stat");
        _procMeminfo = Path.Combine(procRoot, "meminfo");
        _diskRoot = diskRoot;
    }

    public NodeStatsSnapshot Read() => new(
        Guard(ReadCpu, "cpu", ref _cpuLogged),
        Guard(ReadMemory, "memory", ref _memoryLogged),
        Guard(ReadDisk, "disk", ref _diskLogged),
        _membership?.ConnectionCount);

    private double? Guard(Func<double?> read, string name, ref int logged)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref logged, 1) == 0)
            {
                _logger?.LogWarning(ex, "Node stats: reading {Stat} failed; it stays null on the heartbeat.", name);
            }

            return null;
        }
    }

    private double? ReadCpu()
    {
        var current = ParseCpuLine(File.ReadLines(_procStat).First(l => l.StartsWith("cpu ", StringComparison.Ordinal)));
        lock (_cpuLock)
        {
            var previous = _previousCpu;
            _previousCpu = current;
            if (previous is not { } prev || current.Total <= prev.Total)
            {
                return null;
            }

            var total = current.Total - prev.Total;
            var idle = current.Idle >= prev.Idle ? current.Idle - prev.Idle : 0;
            return Percent(total - Math.Min(idle, total), total);
        }
    }

    /// <summary>
    /// Total and idle jiffies from the aggregate <c>cpu</c> line: total is user, nice,
    /// system, idle, iowait, irq, softirq and steal (guest time is already inside user
    /// and nice), idle is idle plus iowait.
    /// </summary>
    internal static (ulong Total, ulong Idle) ParseCpuLine(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Take(8)
            .Select(f => ulong.Parse(f, CultureInfo.InvariantCulture))
            .ToArray();
        if (fields.Length < 4)
        {
            throw new FormatException("The cpu line of /proc/stat has fewer than four counters.");
        }

        ulong total = 0;
        foreach (var f in fields)
        {
            total += f;
        }

        var idle = fields[3] + (fields.Length > 4 ? fields[4] : 0);
        return (total, idle);
    }

    private double? ReadMemory()
    {
        ulong? total = null;
        ulong? available = null;
        foreach (var line in File.ReadLines(_procMeminfo))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                total = ParseMeminfoKb(line);
            }
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                available = ParseMeminfoKb(line);
            }

            if (total is not null && available is not null)
            {
                break;
            }
        }

        if (total is not { } t || available is not { } a || t == 0)
        {
            throw new FormatException("/proc/meminfo has no MemTotal or MemAvailable.");
        }

        return Percent(t - Math.Min(a, t), t);
    }

    private static ulong ParseMeminfoKb(string line) =>
        ulong.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);

    private double? ReadDisk()
    {
        var drive = new DriveInfo(_diskRoot);
        var total = drive.TotalSize;
        if (total <= 0)
        {
            throw new IOException($"The filesystem at {_diskRoot} reports no size.");
        }

        var available = Math.Clamp(drive.AvailableFreeSpace, 0, total);
        return Percent((ulong)(total - available), (ulong)total);
    }

    private static double Percent(ulong part, ulong whole) => Math.Round(100.0 * part / whole, 1);
}
