using Gateway.Api.Instances;

namespace Gateway.Api.Tests;

/// <summary>
/// <see cref="INodeStats"/> for tests: returns <see cref="Next"/> and counts the reads.
/// </summary>
public sealed class FakeNodeStats : INodeStats
{
    public NodeStatsSnapshot Next { get; set; } = NodeStatsSnapshot.Empty;

    public int Reads { get; private set; }

    public NodeStatsSnapshot Read()
    {
        Reads++;
        return Next;
    }
}
