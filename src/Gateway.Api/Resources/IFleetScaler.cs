namespace Gateway.Api.Resources;

/// <summary>An Auto Scaling group's sizes as <see cref="IFleetScaler.DescribeGroupAsync"/> reads them.</summary>
/// <param name="Min">Minimum size.</param>
/// <param name="Desired">Desired capacity.</param>
/// <param name="Max">Maximum size.</param>
/// <param name="InService">Instances in the <c>InService</c> lifecycle state.</param>
public sealed record FleetGroupSize(int Min, int Desired, int Max, int InService);

/// <summary>A launch template's <c>$Default</c> version and its instance type.</summary>
/// <param name="TemplateId">The launch template id.</param>
/// <param name="DefaultVersion">The version number <c>$Default</c> points at.</param>
/// <param name="InstanceType">That version's <c>InstanceType</c>, or null when it sets none.</param>
public sealed record FleetLaunchTemplate(string TemplateId, long DefaultVersion, string? InstanceType);

/// <summary>Instance refresh preferences (docs/load.md section 9: the deploy preferences).</summary>
/// <param name="MinHealthyPercentage">Minimum healthy percentage, 100 for the deploy preferences.</param>
/// <param name="MaxHealthyPercentage">Maximum healthy percentage, 150 for the deploy preferences.</param>
/// <param name="InstanceWarmupSeconds">Instance warmup, 300 s for the deploy preferences.</param>
public sealed record FleetRefreshPreferences(int MinHealthyPercentage, int MaxHealthyPercentage, int InstanceWarmupSeconds);

/// <summary>One instance refresh as <see cref="IFleetScaler.DescribeInstanceRefreshesAsync"/> reads it.</summary>
/// <param name="RefreshId">The refresh id.</param>
/// <param name="Status">The Auto Scaling status, e.g. <c>InProgress</c> or <c>Successful</c>.</param>
/// <param name="PercentComplete">Percentage complete, or null when not reported.</param>
public sealed record FleetRefresh(string RefreshId, string Status, int? PercentComplete);

/// <summary>
/// The Auto Scaling and launch template operations the load feature and the Resources
/// page need (docs/load.md sections 3, 6 and 9). The gateway steers groups and
/// templates that already exist; it never creates or deletes one. The load scheduler
/// uses it for the generator group and the fleet group; the pre-flight checks read
/// through it.
/// <para>
/// Behind an interface so the build/test box, which has no AWS access, substitutes a
/// fake. <see cref="NullFleetScaler"/> is the only registration until the AWS
/// implementation lands; every member of it throws <see cref="NotSupportedException"/>.
/// </para>
/// </summary>
public interface IFleetScaler
{
    /// <summary>The named group's sizes, or null when no such group exists.</summary>
    Task<FleetGroupSize?> DescribeGroupAsync(string name, CancellationToken ct = default);

    /// <summary>Set min, desired and max of the named group in one update.</summary>
    Task SetGroupSizeAsync(string name, int min, int desired, int max, CancellationToken ct = default);

    /// <summary>Set only the desired capacity of the named group.</summary>
    Task SetDesiredAsync(string name, int desired, CancellationToken ct = default);

    /// <summary>The template's <c>$Default</c> version and instance type, or null when no such template exists.</summary>
    Task<FleetLaunchTemplate?> DescribeLaunchTemplateAsync(string template, CancellationToken ct = default);

    /// <summary>
    /// Create a template version from <c>$Default</c> with only <c>InstanceType</c>
    /// changed; returns the new version number.
    /// </summary>
    Task<long> CreateLaunchTemplateVersionAsync(string template, string instanceType, CancellationToken ct = default);

    /// <summary>Point the template's <c>$Default</c> at <paramref name="version"/>.</summary>
    Task SetDefaultLaunchTemplateVersionAsync(string template, long version, CancellationToken ct = default);

    /// <summary>Start an instance refresh of the named group; returns the refresh id.</summary>
    Task<string> StartInstanceRefreshAsync(string group, FleetRefreshPreferences preferences, CancellationToken ct = default);

    /// <summary>Cancel the named group's instance refresh in flight.</summary>
    Task CancelInstanceRefreshAsync(string group, CancellationToken ct = default);

    /// <summary>The named group's instance refreshes, newest first.</summary>
    Task<IReadOnlyList<FleetRefresh>> DescribeInstanceRefreshesAsync(string group, CancellationToken ct = default);
}
