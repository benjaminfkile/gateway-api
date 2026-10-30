namespace Gateway.Api.Data;

/// <summary>
/// One change the gateway made to a resource: today the orphan guard setting the
/// generator group to 0 (docs/load.md section 3). Rows written by earlier releases (fleet,
/// cache and database changes) stay as history. Table <c>resource_audit</c>.
/// </summary>
public class ResourceAudit
{
    /// <summary>Identity primary key.</summary>
    public long Id { get; set; }

    /// <summary>When the change was made.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>Cognito username or client id, or <c>gateway</c> for a change the gateway made itself.</summary>
    public string Actor { get; set; } = default!;

    /// <summary><c>generators</c>; older rows also carry <c>fleet</c>, <c>cache</c> or <c>database</c>.</summary>
    public string Resource { get; set; } = default!;

    /// <summary><c>orphan_guard</c>; older rows also carry <c>size</c>, <c>class</c>, <c>class_rollback</c>, <c>refresh_ended</c>, <c>node_type</c>.</summary>
    public string Action { get; set; } = default!;

    /// <summary>The resource's state before the change, as one line of text.</summary>
    public string? Before { get; set; }

    /// <summary>The state the change asked for, as one line of text.</summary>
    public string? After { get; set; }

    /// <summary>A JSON object with the change's details (refresh id, notes), or null.</summary>
    public string? Detail { get; set; }
}
