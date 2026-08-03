namespace Gateway.Api.Data;

/// <summary>
/// Audit record for a single deploy/rollback/lifecycle action (tech-spec §4.4).
/// </summary>
public class DeployHistory
{
    /// <summary>Surrogate primary key.</summary>
    public int Id { get; set; }

    /// <summary>Target service name.</summary>
    public string Service { get; set; } = default!;

    /// <summary>Digest before the action. Null for a first deploy.</summary>
    public string? FromDigest { get; set; }

    /// <summary>Digest after the action.</summary>
    public string? ToDigest { get; set; }

    /// <summary>Cognito username (or machine principal) that initiated the action.</summary>
    public string Actor { get; set; } = default!;

    /// <summary>Action performed, e.g. 'deploy', 'rollback', 'stop', 'start'.</summary>
    public string Action { get; set; } = default!;

    /// <summary>Overall status, e.g. 'in_progress', 'complete', 'partial', 'failed'.</summary>
    public string Status { get; set; } = default!;

    /// <summary>When the action started.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When the action finished. Null while in progress.</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Free-form detail payload, stored as jsonb in Postgres.</summary>
    public string? Detail { get; set; }
}
