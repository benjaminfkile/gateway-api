using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Jobs;

/// <summary>
/// Run metrics for scheduled jobs (docs/jobs.md section 8): one count per finished
/// run by job and status, and the run duration by job. Behind an interface so the
/// scheduler tests can record calls without a CloudWatch agent.
/// </summary>
public interface IJobMetrics
{
    /// <summary>
    /// Record one run reaching a terminal <paramref name="status"/>. The duration is
    /// null when the run never started (for example a run cancelled while queued).
    /// </summary>
    void RunFinished(string job, string status, TimeSpan? duration);
}

/// <summary>
/// Production <see cref="IJobMetrics"/>. Each finished run is recorded twice:
/// <list type="bullet">
/// <item>on the in-process <see cref="Meter"/> <see cref="MeterName"/>
/// (<see cref="RunCountMetric"/> tagged job and status, <see cref="RunDurationMetric"/>
/// tagged job), readable with any .NET metrics listener;</item>
/// <item>as one CloudWatch embedded-metric-format document sent over UDP to the
/// CloudWatch agent's EMF listener on the node, the same agent the bootstrap step
/// configures for the node metrics, so the numbers land in the gateway's metric
/// namespace (<see cref="JobSchedulerOptions.MetricsNamespace"/>).</item>
/// </list>
/// A send failure is logged at debug and dropped: metrics never affect a run.
/// </summary>
public sealed class JobMetrics : IJobMetrics, IDisposable
{
    public const string MeterName = "Gateway.Jobs";
    public const string RunCountMetric = "JobRunCount";
    public const string RunDurationMetric = "JobRunDuration";

    /// <summary>Log group the agent writes the EMF documents to (under the gateway's <c>/gateway/</c> prefix).</summary>
    public const string MetricsLogGroup = "/gateway/metrics";

    private readonly Meter _meter;
    private readonly Counter<long> _runs;
    private readonly Histogram<double> _duration;
    private readonly JobSchedulerOptions _options;
    private readonly ILogger<JobMetrics> _logger;
    private readonly TimeProvider _time;
    private readonly object _socketGate = new();
    private Socket? _socket;

    public JobMetrics(JobSchedulerOptions options, ILogger<JobMetrics> logger, TimeProvider? time = null)
    {
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _meter = new Meter(MeterName);
        _runs = _meter.CreateCounter<long>(RunCountMetric, unit: "{run}", description: "Finished job runs by job and status.");
        _duration = _meter.CreateHistogram<double>(RunDurationMetric, unit: "s", description: "Job run duration by job.");
    }

    public void RunFinished(string job, string status, TimeSpan? duration)
    {
        _runs.Add(1, new KeyValuePair<string, object?>("job", job), new KeyValuePair<string, object?>("status", status));
        if (duration is { } d)
        {
            _duration.Record(Math.Max(0, d.TotalSeconds), new KeyValuePair<string, object?>("job", job));
        }

        if (string.IsNullOrWhiteSpace(_options.MetricsEndpoint))
        {
            return;
        }

        try
        {
            var payload = Encoding.UTF8.GetBytes(BuildEmf(_options.MetricsNamespace, job, status, duration, _time.GetUtcNow()));
            var endpoint = ParseEndpoint(_options.MetricsEndpoint);
            lock (_socketGate)
            {
                _socket ??= new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                _socket.SendTo(payload, endpoint);
            }
        }
        catch (Exception ex) when (ex is SocketException or FormatException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Dropped job metrics for {Job}: the CloudWatch agent EMF endpoint is unreachable.", job);
        }
    }

    /// <summary>
    /// One embedded-metric-format document: <see cref="RunCountMetric"/> = 1 with
    /// dimensions Job and Status, and <see cref="RunDurationMetric"/> in seconds with
    /// dimension Job when the run has a duration.
    /// </summary>
    public static string BuildEmf(string metricsNamespace, string job, string status, TimeSpan? duration, DateTimeOffset at)
    {
        var directives = new JsonArray
        {
            new JsonObject
            {
                ["Namespace"] = metricsNamespace,
                ["Dimensions"] = new JsonArray { new JsonArray { "Job", "Status" } },
                ["Metrics"] = new JsonArray { new JsonObject { ["Name"] = RunCountMetric, ["Unit"] = "Count" } },
            },
        };

        var document = new JsonObject
        {
            ["_aws"] = new JsonObject
            {
                ["Timestamp"] = at.ToUnixTimeMilliseconds(),
                ["LogGroupName"] = MetricsLogGroup,
                ["CloudWatchMetrics"] = directives,
            },
            ["Job"] = job,
            ["Status"] = status,
            [RunCountMetric] = 1,
        };

        if (duration is { } d)
        {
            directives.Add(new JsonObject
            {
                ["Namespace"] = metricsNamespace,
                ["Dimensions"] = new JsonArray { new JsonArray { "Job" } },
                ["Metrics"] = new JsonArray { new JsonObject { ["Name"] = RunDurationMetric, ["Unit"] = "Seconds" } },
            });
            document[RunDurationMetric] = Math.Round(Math.Max(0, d.TotalSeconds), 3);
        }

        return document.ToJsonString();
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
