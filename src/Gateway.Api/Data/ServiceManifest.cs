namespace Gateway.Api.Data;

/// <summary>
/// Desired-state row for a single downstream service (tech-spec §4.4).
/// Source of truth for what each reconciler converges toward; mutated only by
/// the Management API.
/// </summary>
public class ServiceManifest
{
    /// <summary>Service name, e.g. 'svc-a'. Primary key.</summary>
    public string Name { get; set; } = default!;

    /// <summary>Registry repository for the image.</summary>
    public string Image { get; set; } = default!;

    /// <summary>Image tag, 'latest' or a pinned value.</summary>
    public string Tag { get; set; } = default!;

    /// <summary>Resolved sha256 digest, set on deploy. Null until first resolved.</summary>
    public string? Digest { get; set; }

    /// <summary>Container port exposed on the internal Docker network.</summary>
    public int Port { get; set; }

    /// <summary>Desired lifecycle state: 'running' or 'stopped'.</summary>
    public string DesiredStatus { get; set; } = "running";

    /// <summary>Optional secrets-store reference for the container's env.</summary>
    public string? EnvSecretRef { get; set; }

    /// <summary>Whether this service participates in the aggregated health check.</summary>
    public bool IncludeInHealth { get; set; }

    /// <summary>Cognito username of the last mutator.</summary>
    public string UpdatedBy { get; set; } = default!;

    /// <summary>Timestamp of the last mutation.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
