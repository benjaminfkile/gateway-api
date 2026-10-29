using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Gateway.Api.Jobs;

namespace Gateway.Api.Load;

/// <summary>
/// Load run metrics (docs/load.md section 12): one count per finished run by scenario
/// and status, the run duration by scenario, and one count per generator stats line
/// received. Behind an interface so the scheduler and endpoint tests record calls
/// without a CloudWatch agent.
/// </summary>
public interface ILoadMetrics
{
    /// <summary>
    /// Record one run reaching a terminal <paramref name="status"/>. The duration is
    /// from <c>started_at</c> to <c>finished_at</c>, null when the run never started.
    /// </summary>
    void RunFinished(string scenario, string status, TimeSpan? duration);

    /// <summary>Record one generator stats line stored for a run of <paramref name="scenario"/>.</summary>
    void GeneratorStatsReceived(string scenario);
}

/// <summary>
/// Production <see cref="ILoadMetrics"/>, the same two paths as <see cref="JobMetrics"/>:
/// the in-process <see cref="Meter"/> <see cref="MeterName"/>, and one CloudWatch
/// embedded-metric-format document per event sent over UDP to the CloudWatch agent's
/// EMF listener (<c>Jobs:MetricsEndpoint</c>, namespace <c>Jobs:MetricsNamespace</c>),
/// written to the <see cref="JobMetrics.MetricsLogGroup"/> group. An empty endpoint
/// turns the send off. A send failure is logged at debug and dropped: metrics never
/// affect a run.
/// </summary>
public sealed class LoadMetrics : ILoadMetrics, IDisposable
{
    public const string MeterName = "Gateway.Load";
    public const string RunCountMetric = "LoadRunCount";
    public const string RunDurationMetric = "LoadRunDuration";
    public const string GeneratorStatsMetric = "LoadGeneratorStats";

    private readonly Meter _meter;
    private readonly Counter<long> _runs;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _lines;
    private readonly string? _endpoint;
    private readonly string _namespace;
    private readonly ILogger<LoadMetrics> _logger;
    private readonly TimeProvider _time;
    private readonly object _socketGate = new();
    private Socket? _socket;

    public LoadMetrics(string? endpoint, string metricsNamespace, ILogger<LoadMetrics> logger, TimeProvider? time = null)
    {
        _endpoint = endpoint;
        _namespace = metricsNamespace;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _meter = new Meter(MeterName);
        _runs = _meter.CreateCounter<long>(RunCountMetric, unit: "{run}", description: "Finished load runs by scenario and status.");
        _duration = _meter.CreateHistogram<double>(RunDurationMetric, unit: "s", description: "Load run duration by scenario.");
        _lines = _meter.CreateCounter<long>(GeneratorStatsMetric, unit: "{line}", description: "Generator stats lines received by scenario.");
    }

    public void RunFinished(string scenario, string status, TimeSpan? duration)
    {
        _runs.Add(1, new KeyValuePair<string, object?>("scenario", scenario), new KeyValuePair<string, object?>("status", status));
        if (duration is { } d)
        {
            _duration.Record(Math.Max(0, d.TotalSeconds), new KeyValuePair<string, object?>("scenario", scenario));
        }

        Send(scenario, () => BuildRunEmf(_namespace, scenario, status, duration, _time.GetUtcNow()));
    }

    public void GeneratorStatsReceived(string scenario)
    {
        _lines.Add(1, new KeyValuePair<string, object?>("scenario", scenario));
        Send(scenario, () => BuildStatsEmf(_namespace, scenario, _time.GetUtcNow()));
    }

    /// <summary>
    /// The document of a finished run: <see cref="RunCountMetric"/> = 1 with dimensions
    /// Scenario and Status, and <see cref="RunDurationMetric"/> in seconds with dimension
    /// Scenario when the run has a duration.
    /// </summary>
    public static string BuildRunEmf(string metricsNamespace, string scenario, string status, TimeSpan? duration, DateTimeOffset at)
    {
        var directives = new JsonArray
        {
            Directive(metricsNamespace, new JsonArray { "Scenario", "Status" }, RunCountMetric, "Count"),
        };

        var document = Envelope(directives, at);
        document["Scenario"] = scenario;
        document["Status"] = status;
        document[RunCountMetric] = 1;

        if (duration is { } d)
        {
            directives.Add(Directive(metricsNamespace, new JsonArray { "Scenario" }, RunDurationMetric, "Seconds"));
            document[RunDurationMetric] = Math.Round(Math.Max(0, d.TotalSeconds), 3);
        }

        return document.ToJsonString();
    }

    /// <summary>The document of one stats line: <see cref="GeneratorStatsMetric"/> = 1 with dimension Scenario.</summary>
    public static string BuildStatsEmf(string metricsNamespace, string scenario, DateTimeOffset at)
    {
        var document = Envelope(new JsonArray
        {
            Directive(metricsNamespace, new JsonArray { "Scenario" }, GeneratorStatsMetric, "Count"),
        }, at);
        document["Scenario"] = scenario;
        document[GeneratorStatsMetric] = 1;
        return document.ToJsonString();
    }

    private static JsonObject Directive(string metricsNamespace, JsonArray dimensions, string metric, string unit) => new()
    {
        ["Namespace"] = metricsNamespace,
        ["Dimensions"] = new JsonArray { dimensions },
        ["Metrics"] = new JsonArray { new JsonObject { ["Name"] = metric, ["Unit"] = unit } },
    };

    private static JsonObject Envelope(JsonArray directives, DateTimeOffset at) => new()
    {
        ["_aws"] = new JsonObject
        {
            ["Timestamp"] = at.ToUnixTimeMilliseconds(),
            ["LogGroupName"] = JobMetrics.MetricsLogGroup,
            ["CloudWatchMetrics"] = directives,
        },
    };

    private void Send(string scenario, Func<string> build)
    {
        if (string.IsNullOrWhiteSpace(_endpoint))
        {
            return;
        }

        try
        {
            var payload = Encoding.UTF8.GetBytes(build());
            var endpoint = ParseEndpoint(_endpoint);
            lock (_socketGate)
            {
                _socket ??= new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                _socket.SendTo(payload, endpoint);
            }
        }
        catch (Exception ex) when (ex is SocketException or FormatException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Dropped load metrics for {Scenario}: the CloudWatch agent EMF endpoint is unreachable.", scenario);
        }
    }

    private static IPEndPoint ParseEndpoint(string value)
    {
        if (IPEndPoint.TryParse(value.Trim(), out var endpoint) && endpoint.Port != 0)
        {
            return endpoint;
        }

        throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"'{value}' is not an ip:port endpoint."));
    }

    public void Dispose()
    {
        lock (_socketGate)
        {
            _socket?.Dispose();
            _socket = null;
        }

        _meter.Dispose();
    }
}
