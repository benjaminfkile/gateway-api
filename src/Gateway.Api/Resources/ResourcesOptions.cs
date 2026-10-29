namespace Gateway.Api.Resources;

/// <summary>
/// Resources page configuration (docs/load.md section 9), bound from the
/// <c>Resources</c> configuration section. Each class list is a comma separated string
/// parsed once into a set. Resolved lazily from <see cref="IConfiguration"/> so a test
/// host's late-bound settings are honoured.
/// </summary>
public sealed class ResourcesOptions
{
    /// <summary>Configuration section the options bind from.</summary>
    public const string SectionName = "Resources";

    /// <summary>The fleet Auto Scaling group name key.</summary>
    public const string FleetGroupKey = "Resources:FleetGroup";

    /// <summary>The fleet launch template name or id key.</summary>
    public const string FleetTemplateKey = "Resources:FleetTemplate";

    /// <summary>The allowed fleet classes key.</summary>
    public const string FleetClassesKey = "Resources:FleetClasses";

    /// <summary>The largest fleet size key.</summary>
    public const string FleetMaxSizeKey = "Resources:FleetMaxSize";

    /// <summary>The backplane replication group key.</summary>
    public const string CacheReplicationGroupKey = "Resources:CacheReplicationGroup";

    /// <summary>The allowed cache node types key.</summary>
    public const string CacheNodeTypesKey = "Resources:CacheNodeTypes";

    /// <summary>The database instance identifier key.</summary>
    public const string DatabaseInstanceKey = "Resources:DatabaseInstance";

    /// <summary>The allowed database classes key.</summary>
    public const string DatabaseClassesKey = "Resources:DatabaseClasses";

    /// <summary>Default of <see cref="FleetMaxSize"/>.</summary>
    public const int DefaultFleetMaxSize = 50;

    /// <summary>
    /// The fleet Auto Scaling group name (<c>Resources:FleetGroup</c>). When unset and
    /// <see cref="FleetTemplate"/> is set, <see cref="FleetGroupResolver"/> reads it from
    /// the instance's <c>aws:autoscaling:groupName</c> tag.
    /// </summary>
    public string? FleetGroup { get; init; }

    /// <summary>The fleet launch template name or id (<c>Resources:FleetTemplate</c>).</summary>
    public string? FleetTemplate { get; init; }

    /// <summary>Allowed fleet instance classes (<c>Resources:FleetClasses</c>).</summary>
    public IReadOnlySet<string> FleetClasses { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Largest size <c>PUT fleet/size</c> accepts (<c>Resources:FleetMaxSize</c>, default 50).</summary>
    public int FleetMaxSize { get; init; } = DefaultFleetMaxSize;

    /// <summary>The backplane replication group id (<c>Resources:CacheReplicationGroup</c>).</summary>
    public string? CacheReplicationGroup { get; init; }

    /// <summary>Allowed cache node types (<c>Resources:CacheNodeTypes</c>).</summary>
    public IReadOnlySet<string> CacheNodeTypes { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The database instance identifier (<c>Resources:DatabaseInstance</c>).</summary>
    public string? DatabaseInstance { get; init; }

    /// <summary>Allowed database instance classes (<c>Resources:DatabaseClasses</c>).</summary>
    public IReadOnlySet<string> DatabaseClasses { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>True when the fleet group may be known: a group or a template is configured.</summary>
    public bool FleetConfigured => FleetGroup is not null || FleetTemplate is not null;

    /// <summary>True when the backplane replication group is configured.</summary>
    public bool CacheConfigured => CacheReplicationGroup is not null;

    /// <summary>True when the database instance is configured.</summary>
    public bool DatabaseConfigured => DatabaseInstance is not null;

    /// <summary>Read the <c>Resources</c> keys, parsing each class list into a set.</summary>
    public static ResourcesOptions FromConfiguration(IConfiguration configuration)
    {
        var maxSize = int.TryParse(configuration[FleetMaxSizeKey], out var parsed) && parsed >= 1
            ? parsed
            : DefaultFleetMaxSize;

        return new ResourcesOptions
        {
            FleetGroup = NonEmpty(configuration[FleetGroupKey]),
            FleetTemplate = NonEmpty(configuration[FleetTemplateKey]),
            FleetClasses = ParseList(configuration[FleetClassesKey]),
            FleetMaxSize = maxSize,
            CacheReplicationGroup = NonEmpty(configuration[CacheReplicationGroupKey]),
            CacheNodeTypes = ParseList(configuration[CacheNodeTypesKey]),
            DatabaseInstance = NonEmpty(configuration[DatabaseInstanceKey]),
            DatabaseClasses = ParseList(configuration[DatabaseClassesKey]),
        };
    }

    /// <summary>A comma separated list as a set: entries trimmed, empty entries dropped.</summary>
    public static IReadOnlySet<string> ParseList(string? value) =>
        (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
