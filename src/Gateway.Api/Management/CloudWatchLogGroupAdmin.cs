using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Microsoft.Extensions.Logging;

namespace Gateway.Api.Management;

/// <summary>
/// <see cref="ILogGroupAdmin"/> over Amazon CloudWatch Logs. Sets group retention
/// via <c>PutRetentionPolicy</c> (tech-spec §9). Registered lazily in production so
/// a gateway with no AWS region configured still boots — the client is constructed
/// only when the reconciler first sets retention.
/// </summary>
public sealed class CloudWatchLogGroupAdmin : ILogGroupAdmin
{
    private readonly IAmazonCloudWatchLogs _logs;
    private readonly ILogger<CloudWatchLogGroupAdmin> _logger;

    public CloudWatchLogGroupAdmin(IAmazonCloudWatchLogs logs, ILogger<CloudWatchLogGroupAdmin> logger)
    {
        _logs = logs;
        _logger = logger;
    }

    public async Task EnsureRetentionAsync(string logGroup, int retentionDays, CancellationToken ct = default)
    {
        try
        {
            await _logs.PutRetentionPolicyAsync(new PutRetentionPolicyRequest
            {
                LogGroupName = logGroup,
                RetentionInDays = retentionDays,
            }, ct);
        }
        catch (AmazonCloudWatchLogsException ex) when (
            string.Equals(ex.ErrorCode, "AccessDeniedException", StringComparison.Ordinal))
        {
            // IAM may lag a freshly created group (tech-spec §9): the awslogs driver
            // gets logs:CreateLogGroup before the instance role is granted
            // logs:PutRetentionPolicy. Tolerate it with a clear warning — logs still
            // ship; a later loop retries once the grant lands.
            _logger.LogWarning(ex,
                "Access denied setting {Days}-day retention on log group {Group}; the instance role may "
                + "lack logs:PutRetentionPolicy (IAM may lag). Logs still ship via the awslogs driver.",
                retentionDays, logGroup);
        }
    }
}
