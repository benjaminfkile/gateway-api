namespace Gateway.Api.Management;

/// <summary>
/// Administers centralized log groups (tech-spec §9). The awslogs driver's
/// <c>awslogs-create-group=true</c> creates a service's group on first write but
/// <b>cannot</b> set retention, so after a container start that may have created the
/// group the reconciler sets 30-day retention once per group through this seam.
/// <para>
/// Behind an interface so the build/test box — no AWS access — substitutes a fake.
/// The production implementation is <see cref="CloudWatchLogGroupAdmin"/> over
/// AWSSDK.CloudWatchLogs.
/// </para>
/// </summary>
public interface ILogGroupAdmin
{
    /// <summary>
    /// Ensure <paramref name="logGroup"/> has the given retention. Implementations
    /// tolerate a lagging IAM grant (AccessDenied) with a warning rather than
    /// failing the reconcile loop — logs still ship via the driver regardless.
    /// </summary>
    Task EnsureRetentionAsync(string logGroup, int retentionDays, CancellationToken ct = default);
}
