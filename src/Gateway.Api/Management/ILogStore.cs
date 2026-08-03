namespace Gateway.Api.Management;

/// <summary>One log line: a timestamp and the message text.</summary>
/// <param name="Ts">When the line was emitted.</param>
/// <param name="Message">The log message.</param>
public sealed record LogLine(DateTimeOffset Ts, string Message);

/// <summary>
/// Reads centralized service logs (tech-spec §4.3, §4.5). Containers ship to a log
/// group <c>/gateway/services/{service}</c> with one stream per instance, so the
/// dashboard's log viewer works for any instance regardless of which one serves the
/// request and survives instance replacement.
/// <para>
/// Behind an interface so the build/test box — which has no AWS access — can
/// substitute a fake. The production implementation is
/// <see cref="CloudWatchLogStore"/> over AWSSDK.CloudWatchLogs.
/// </para>
/// </summary>
public interface ILogStore
{
    /// <summary>
    /// Fetch up to <paramref name="tail"/> of the most recent log lines for
    /// <paramref name="service"/>, optionally scoped to a single
    /// <paramref name="instance"/>'s stream. Lines are returned oldest-first.
    /// </summary>
    Task<IReadOnlyList<LogLine>> GetLogsAsync(
        string service,
        string? instance,
        int tail,
        CancellationToken ct = default);
}
