using Gateway.Api.Load;

namespace Gateway.Api.Tests;

/// <summary>Records every <see cref="ILoadMetrics"/> call so tests can assert the metrics without a CloudWatch agent.</summary>
public sealed class FakeLoadMetrics : ILoadMetrics
{
    private readonly object _gate = new();
    private readonly List<(string Scenario, string Status, TimeSpan? Duration)> _runs = new();
    private readonly List<string> _lines = new();

    public IReadOnlyList<(string Scenario, string Status, TimeSpan? Duration)> Runs
    {
        get
        {
            lock (_gate)
            {
                return _runs.ToList();
            }
        }
    }

    public IReadOnlyList<string> StatsLines
    {
        get
        {
            lock (_gate)
            {
                return _lines.ToList();
            }
        }
    }

    public void RunFinished(string scenario, string status, TimeSpan? duration)
    {
        lock (_gate)
        {
            _runs.Add((scenario, status, duration));
        }
    }

    public void GeneratorStatsReceived(string scenario)
    {
        lock (_gate)
        {
            _lines.Add(scenario);
        }
    }
}
