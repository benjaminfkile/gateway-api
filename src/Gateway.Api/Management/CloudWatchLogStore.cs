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

    /// <summary>The log group name for a service.</summary>
    public static string LogGroupFor(string service) => LogGroupPrefix + service;

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
            var response = await _logs.GetLogEventsAsync(new GetLogEventsRequest
            {
                LogGroupName = group,
                LogStreamName = instance,
                Limit = tail,
                StartFromHead = false,
            }, ct);

            return (response.Events ?? new List<OutputLogEvent>())
                .Select(e => new LogLine(
                    new DateTimeOffset(DateTime.SpecifyKind(e.Timestamp, DateTimeKind.Utc)),
                    e.Message))
                .ToList();
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
}
