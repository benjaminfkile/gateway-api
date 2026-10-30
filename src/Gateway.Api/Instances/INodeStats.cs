namespace Gateway.Api.Instances;

/// <summary>
/// One reading of this node's stats, carried on the heartbeat row (tech-spec §4.4)
/// and into the load run fleet samples (docs/load.md section 4). Every value is
/// null when it could not be read.
/// </summary>
/// <param name="Cpu">Whole-box cpu busy percent since the previous reading.</param>
/// <param name="Memory">Memory used percent (total minus available, of total).</param>
/// <param name="Disk">Root filesystem used percent (total minus available, of total).</param>
/// <param name="HubConnections">Hub connections this instance holds right now.</param>
public sealed record NodeStatsSnapshot(double? Cpu, double? Memory, double? Disk, int? HubConnections)
{
    /// <summary>A reading with every value null.</summary>
    public static readonly NodeStatsSnapshot Empty = new(null, null, null, null);
}

/// <summary>
/// Measures this node in process: no agent and no AWS call. <see cref="LinuxNodeStats"/>
/// reads <c>/proc</c>, the root filesystem and the hub's connection registry;
/// <see cref="NullNodeStats"/> stands in on Windows and in tests. A read that fails
/// leaves its value null; it never throws.
/// </summary>
public interface INodeStats
{
    /// <summary>
    /// Take one reading. Called once per heartbeat: the cpu figure is the busy share
    /// of the time since the previous call, so the first call has no cpu value.
    /// </summary>
    NodeStatsSnapshot Read();
}

/// <summary>The <see cref="INodeStats"/> for Windows and tests: every value null.</summary>
public sealed class NullNodeStats : INodeStats
{
    public NodeStatsSnapshot Read() => NodeStatsSnapshot.Empty;
}
