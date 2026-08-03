namespace Gateway.Api.Containers;

/// <summary>
/// Log driver configuration applied to every service container the reconciler
/// starts (tech-spec §4.3). Production uses the <c>awslogs</c> driver so
/// container output lands in a centralized log store and survives instance
/// replacement; the options (log group, region, stream prefix, …) come from
/// configuration and are never hard-coded. Tests supply a fake runtime and so
/// ignore these values.
/// </summary>
public sealed class LogDriverConfig
{
    /// <summary>Docker log driver name, e.g. <c>awslogs</c> or <c>json-file</c>.</summary>
    public string Driver { get; set; } = "awslogs";

    /// <summary>Driver-specific options passed through to Docker verbatim.</summary>
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.Ordinal);
}
