namespace Gateway.Api.Data;

/// <summary>
/// One change the Resources API made to a scaled resource, or one fleet refresh the
/// gateway saw end (docs/load.md section 9). Table <c>resource_audit</c>.
/// </summary>
public class ResourceAudit
{
    /// <summary>Identity primary key.</summary>
    public long Id { get; set; }

    /// <summary>When the change was made.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>Cognito username or client id, or <c>gateway</c> for a refresh the watcher closed.</summary>
    public string Actor { get; set; } = default!;

    /// <summary><c>fleet</c>, <c>cache</c> or <c>database</c>.</summary>
    public string Resource { get; set; } = default!;

    /// <summary><c>size</c>, <c>class</c>, <c>class_rollback</c>, <c>refresh_ended</c>, <c>node_type</c>.</summary>
    public string Action { get; set; } = default!;

    /// <summary>The resource's state before the change, as one line of text.</summary>
    public string? Before { get; set; }

    /// <summary>The state the change asked for, as one line of text.</summary>
    public string? After { get; set; }

    /// <summary>A JSON object with the change's details (refresh id, notes), or null.</summary>
    public string? Detail { get; set; }
}
