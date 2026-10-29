namespace Gateway.Api.Resources;

/// <summary>
/// The <see cref="IFleetScaler"/> registered when neither a fleet group, a fleet
/// template nor a generator group is configured (a dev box without AWS). Every member
/// throws <see cref="NotSupportedException"/>, so nothing is ever scaled; the pre-flight
/// checks that read through it report the group or template as unreadable
/// (<c>generator_group</c> red, <c>fleet_class</c> amber "unknown").
/// </summary>
public sealed class NullFleetScaler : IFleetScaler
{
    /// <summary>The message every member throws with.</summary>
    public const string NotConfiguredMessage = "No fleet scaler is configured on this gateway.";

    /// <exception cref="NotSupportedException">Always.</exception>
    public Task<FleetGroupSize?> DescribeGroupAsync(string name, CancellationToken ct = default) =>
        throw new NotSupportedException(NotConfiguredMessage);

    /// <exception cref="NotSupportedException">Always.</exception>
    public Task SetGroupSizeAsync(string name, int min, int desired, int max, CancellationToken ct = default) =>
        throw new NotSupportedException(NotConfiguredMessage);

    /// <exception cref="NotSupportedException">Always.</exception>
    public Task SetDesiredAsync(string name, int desired, CancellationToken ct = default) =>
        throw new NotSupportedException(NotConfiguredMessage);

    /// <exception cref="NotSupportedException">Always.</exception>
    public Task<FleetLaunchTemplate?> DescribeLaunchTemplateAsync(string template, CancellationToken ct = default) =>
        throw new NotSupportedException(NotConfiguredMessage);

    /// <exception cref="NotSupportedException">Always.</exception>
    public Task<FleetTemplateVersion?> DescribeLaunchTemplateVersionAsync(string template, long version, CancellationToken ct = default) =>
        throw new NotSupportedException(NotConfiguredMessage);

    /// <exception cref="NotSupportedException">Always.</exception>
    public Task<long> CreateLaunchTemplateVersionAsync(string template, string instanceType, CancellationToken ct = default) =>
        throw new NotSupportedException(NotConfiguredMessage);

    /// <exception cref="NotSupportedException">Always.</exception>
    public Task SetDefaultLaunchTemplateVersionAsync(string template, long version, CancellationToken ct = default) =>
        throw new NotSupportedException(NotConfiguredMessage);

    /// <exception cref="NotSupportedException">Always.</exception>
    public Task<string> StartInstanceRefreshAsync(string group, FleetRefreshPreferences preferences, CancellationToken ct = default) =>
        throw new NotSupportedException(NotConfiguredMessage);

    /// <exception cref="NotSupportedException">Always.</exception>
    public Task CancelInstanceRefreshAsync(string group, CancellationToken ct = default) =>
        throw new NotSupportedException(NotConfiguredMessage);

    /// <exception cref="NotSupportedException">Always.</exception>
    public Task<IReadOnlyList<FleetRefresh>> DescribeInstanceRefreshesAsync(string group, string? refreshId = null, CancellationToken ct = default) =>
        throw new NotSupportedException(NotConfiguredMessage);
}
