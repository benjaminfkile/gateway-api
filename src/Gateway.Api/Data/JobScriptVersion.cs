namespace Gateway.Api.Data;

/// <summary>
/// One historical script body for a job (docs/jobs.md section 2). A row is written
/// on create and on every upsert that changes the body; keyed by (job, version).
/// </summary>
public class JobScriptVersion
{
    /// <summary>The job name.</summary>
    public string Job { get; set; } = default!;

    /// <summary>The script version this body was saved as.</summary>
    public int Version { get; set; }

    /// <summary>The script body.</summary>
    public string Script { get; set; } = default!;

    /// <summary>Lowercase hex sha256 of the UTF-8 body.</summary>
    public string Sha256 { get; set; } = default!;

    /// <summary>Who saved this version.</summary>
    public string UpdatedBy { get; set; } = default!;

    /// <summary>When this version was saved.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
