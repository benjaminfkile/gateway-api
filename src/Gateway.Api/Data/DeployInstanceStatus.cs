namespace Gateway.Api.Data;

/// <summary>
/// Per-instance rollout progress for a deploy ("17/20 converged"),
/// keyed by (deploy, instance) (tech-spec §4.4).
/// </summary>
public class DeployInstanceStatus
{
    /// <summary>Deploy this progress row belongs to (composite key part).</summary>
    public int DeployId { get; set; }

    /// <summary>Instance reporting progress (composite key part).</summary>
    public string InstanceId { get; set; } = default!;

    /// <summary>Convergence status on this instance, e.g. 'pending', 'converged', 'failed'.</summary>
    public string Status { get; set; } = default!;

    /// <summary>Optional detail, e.g. a failure reason.</summary>
    public string? Detail { get; set; }

    /// <summary>Timestamp of the last update to this row.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
