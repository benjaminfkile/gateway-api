namespace Gateway.Api.Data;

/// <summary>
/// Fleet-awareness heartbeat row, upserted by every reconciler loop
/// (tech-spec §4.4). Rows stale &gt; 90s indicate a departed instance.
/// </summary>
public class InstanceStatus
{
    /// <summary>Instance identifier. Primary key.</summary>
    public string InstanceId { get; set; } = default!;

    /// <summary>Private IP of the instance.</summary>
    public string? PrivateIp { get; set; }

    /// <summary>Public IP of the instance, if any.</summary>
    public string? PublicIp { get; set; }

    /// <summary>Gateway binary version running on the instance.</summary>
    public string? GatewayVer { get; set; }

    /// <summary>
    /// Whether this instance is the fleet leader (tech-spec §4.3): heartbeat-derived
    /// as the lowest live <c>instance_id</c>, written each reconcile loop from that
    /// evaluation so the dashboard leader badge follows automatically.
    /// </summary>
    public bool IsLeader { get; set; }

    /// <summary>
    /// Per-service container inventory, stored as jsonb in Postgres:
    /// [{name, digest, state, startedAt, restarts}].
    /// </summary>
    public string? Services { get; set; }

    /// <summary>Timestamp of the last heartbeat.</summary>
    public DateTimeOffset HeartbeatAt { get; set; }
}
