namespace Gateway.Api.Management;

/// <summary>
/// Well-known <c>deploy_history.status</c> values (tech-spec §4.4, §7). A deploy is
/// written <see cref="InProgress"/>, and the leader marks it <see cref="Done"/> once
/// every live instance has converged or <see cref="Partial"/> when stragglers remain.
/// Immediate lifecycle/audit actions (stop/start/restart/put) land as <see cref="Done"/>.
/// </summary>
public static class DeployStatus
{
    public const string InProgress = "in_progress";
    public const string Done = "done";
    public const string Partial = "partial";
    public const string Failed = "failed";
}

/// <summary>
/// Well-known <c>deploy_history.action</c> values — the audit verb for every
/// mutating Management API call (tech-spec §4.5).
/// </summary>
public static class DeployAction
{
    public const string Deploy = "deploy";
    public const string Rollback = "rollback";
    public const string Stop = "stop";
    public const string Start = "start";
    public const string Restart = "restart";
    public const string Upsert = "upsert";
}

/// <summary>Well-known <c>deploy_instance_status.status</c> values (tech-spec §4.4).</summary>
public static class DeployInstanceState
{
    public const string Pending = "pending";
    public const string Converged = "converged";
    public const string Failed = "failed";
}
