using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;

namespace Gateway.Api.Management;

/// <summary>
/// <see cref="ILogStore"/> over Amazon CloudWatch Logs (AWSSDK.CloudWatchLogs).
/// Reads from log group <c>/gateway/services/{service}</c> — one stream per
/// instance (tech-spec §4.3, §4.5). Registered lazily in production so a gateway
/// with no AWS region configured still boots.
/// </summary>
public sealed class CloudWatchLogStore : ILogStore
{
    /// <summary>Log-group prefix; the service name is appended (tech-spec §4.3).</summary>
    public const string LogGroupPrefix = "/gateway/services/";

    private readonly IAmazonCloudWatchLogs _logs;

    public CloudWatchLogStore(IAmazonCloudWatchLogs logs)
    {
        _logs = logs;
    }

    /// <summary>
    /// The log group name for a service. This is the single source of truth for the
    /// group template: the reconciler's awslogs write path builds the same value via
    /// <c>LogConfigFactory.ForService</c>, so read and write can never diverge
    /// (tech-spec §4.3). The <c>instance</c> query param below is the stream name,
    /// matching the awslogs <c>awslogs-stream</c> = instance id.
    /// </summary>
    public static string LogGroupFor(string service) => LogGroupPrefix + service;

    /// <summary>Log-group prefix for scheduled jobs; the job name is appended (docs/jobs.md section 3).</summary>
    public const string JobLogGroupPrefix = "/gateway/jobs/";

    /// <summary>Log-stream prefix for a job run; the run id is appended.</summary>
    public const string JobRunStreamPrefix = "run-";

    /// <summary>The log group a job's runs write to: <c>/gateway/jobs/{job}</c>.</summary>
    public static string JobLogGroupFor(string job) => JobLogGroupPrefix + job;

    /// <summary>The log stream of one job run: <c>run-{id}</c>.</summary>
    public static string JobRunStreamFor(long runId) => JobRunStreamPrefix + runId;

    /// <summary>The log group every load generator writes to (docs/load.md section 5).</summary>
    public const string LoadLogGroup = "/gateway/load";

    /// <summary>The log stream of one generator of a load run: <c>run-{id}-{index}</c>.</summary>
    public static string LoadRunStreamFor(long runId, int index) => $"{JobRunStreamPrefix}{runId}-{index}";

    public async Task<IReadOnlyList<LogLine>> GetLogsAsync(
        string service,
        string? instance,
        int tail,
        CancellationToken ct = default)
    {
        var group = LogGroupFor(service);

        if (!string.IsNullOrEmpty(instance))
        {
            // A specific instance's stream: get the most recent `tail` events.
            return await GetStreamLogsAsync(group, instance, tail, ct);
        }

        // No instance: pull the most recent `tail` events across every stream.
        var filtered = await _logs.FilterLogEventsAsync(new FilterLogEventsRequest
        {
            LogGroupName = group,
            Limit = tail,
        }, ct);

        return (filtered.Events ?? new List<FilteredLogEvent>())
            .Select(e => new LogLine(DateTimeOffset.FromUnixTimeMilliseconds(e.Timestamp), e.Message))
            .OrderBy(l => l.Ts)
            .ToList();
    }

    public async Task<IReadOnlyList<LogLine>> GetStreamLogsAsync(
        string group,
        string stream,
        int tail,
        CancellationToken ct = default)
    {
        var response = await _logs.GetLogEventsAsync(new GetLogEventsRequest
        {
            LogGroupName = group,
            LogStreamName = stream,
            Limit = tail,
            StartFromHead = false,
        }, ct);

        return (response.Events ?? new List<OutputLogEvent>())
            .Select(e => new LogLine(
                new DateTimeOffset(DateTime.SpecifyKind(e.Timestamp, DateTimeKind.Utc)),
                e.Message))
            .ToList();
    }
}
