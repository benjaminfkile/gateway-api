using Amazon.AutoScaling;
using Amazon.AutoScaling.Model;
using Amazon.EC2;
using Amazon.EC2.Model;

namespace Gateway.Api.Resources;

/// <summary>
/// <see cref="IFleetScaler"/> over Amazon EC2 Auto Scaling (AWSSDK.AutoScaling) for the
/// groups and Amazon EC2 (AWSSDK.EC2) for the launch template. The clients come from
/// the container: default credential chain, region from the environment. A template
/// argument that starts with <c>lt-</c> is a launch template id, anything else a name.
/// An unknown group, template or version reads as null; every other AWS error is
/// thrown to the caller.
/// </summary>
public sealed class AwsFleetScaler : IFleetScaler
{
    /// <summary>The <c>$Default</c> version selector.</summary>
    public const string DefaultVersion = "$Default";

    private readonly IAmazonAutoScaling _autoScaling;
    private readonly IAmazonEC2 _ec2;

    public AwsFleetScaler(IAmazonAutoScaling autoScaling, IAmazonEC2 ec2)
    {
        _autoScaling = autoScaling;
        _ec2 = ec2;
    }

    public async Task<FleetGroupSize?> DescribeGroupAsync(string name, CancellationToken ct = default)
    {
        var response = await _autoScaling.DescribeAutoScalingGroupsAsync(new DescribeAutoScalingGroupsRequest
        {
            AutoScalingGroupNames = new List<string> { name },
        }, ct);

        var group = response.AutoScalingGroups?.FirstOrDefault(g =>
            string.Equals(g.AutoScalingGroupName, name, StringComparison.Ordinal));
        if (group is null)
        {
            return null;
        }

        var inService = group.Instances?.Count(i =>
            string.Equals(i.LifecycleState?.Value, LifecycleState.InService.Value, StringComparison.Ordinal)) ?? 0;
        return new FleetGroupSize(group.MinSize, group.DesiredCapacity, group.MaxSize, inService);
    }

    public Task SetGroupSizeAsync(string name, int min, int desired, int max, CancellationToken ct = default) =>
        _autoScaling.UpdateAutoScalingGroupAsync(new UpdateAutoScalingGroupRequest
        {
            AutoScalingGroupName = name,
            MinSize = min,
            DesiredCapacity = desired,
            MaxSize = max,
        }, ct);

    public Task SetDesiredAsync(string name, int desired, CancellationToken ct = default) =>
        _autoScaling.SetDesiredCapacityAsync(new SetDesiredCapacityRequest
        {
            AutoScalingGroupName = name,
            DesiredCapacity = desired,
            HonorCooldown = false,
        }, ct);

    public async Task<FleetLaunchTemplate?> DescribeLaunchTemplateAsync(string template, CancellationToken ct = default)
    {
        var version = await DescribeVersionAsync(template, DefaultVersion, ct);
        return version is null
            ? null
            : new FleetLaunchTemplate(
                version.LaunchTemplateId,
                version.VersionNumber,
                NonEmpty(version.LaunchTemplateData?.InstanceType?.Value),
                NonEmpty(version.VersionDescription));
    }

    public async Task<FleetTemplateVersion?> DescribeLaunchTemplateVersionAsync(string template, long version, CancellationToken ct = default)
    {
        var described = await DescribeVersionAsync(template, version.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        return described is null
            ? null
            : new FleetTemplateVersion(
                described.VersionNumber,
                NonEmpty(described.LaunchTemplateData?.InstanceType?.Value),
                NonEmpty(described.VersionDescription));
    }

    public async Task<long> CreateLaunchTemplateVersionAsync(string template, string instanceType, CancellationToken ct = default)
    {
        // SourceVersion copies every field of $Default; the request data overrides
        // only InstanceType.
        var request = new CreateLaunchTemplateVersionRequest
        {
            SourceVersion = DefaultVersion,
            VersionDescription = $"Resources page: InstanceType {instanceType}",
            LaunchTemplateData = new RequestLaunchTemplateData { InstanceType = InstanceType.FindValue(instanceType) },
        };
        if (IsTemplateId(template))
        {
            request.LaunchTemplateId = template;
        }
        else
        {
            request.LaunchTemplateName = template;
        }

        var response = await _ec2.CreateLaunchTemplateVersionAsync(request, ct);
        return response.LaunchTemplateVersion.VersionNumber;
    }

    public Task SetDefaultLaunchTemplateVersionAsync(string template, long version, CancellationToken ct = default)
    {
        var request = new ModifyLaunchTemplateRequest
        {
            DefaultVersion = version.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (IsTemplateId(template))
        {
            request.LaunchTemplateId = template;
        }
        else
        {
            request.LaunchTemplateName = template;
        }

        return _ec2.ModifyLaunchTemplateAsync(request, ct);
    }

    public async Task<string> StartInstanceRefreshAsync(string group, FleetRefreshPreferences preferences, CancellationToken ct = default)
    {
        var response = await _autoScaling.StartInstanceRefreshAsync(new StartInstanceRefreshRequest
        {
            AutoScalingGroupName = group,
            Preferences = new RefreshPreferences
            {
                MinHealthyPercentage = preferences.MinHealthyPercentage,
                MaxHealthyPercentage = preferences.MaxHealthyPercentage,
                InstanceWarmup = preferences.InstanceWarmupSeconds,
            },
        }, ct);
        return response.InstanceRefreshId;
    }

    public Task CancelInstanceRefreshAsync(string group, CancellationToken ct = default) =>
        _autoScaling.CancelInstanceRefreshAsync(new CancelInstanceRefreshRequest
        {
            AutoScalingGroupName = group,
        }, ct);

    public async Task<IReadOnlyList<FleetRefresh>> DescribeInstanceRefreshesAsync(string group, string? refreshId = null, CancellationToken ct = default)
    {
        var request = new DescribeInstanceRefreshesRequest { AutoScalingGroupName = group };
        if (!string.IsNullOrEmpty(refreshId))
        {
            request.InstanceRefreshIds = new List<string> { refreshId };
        }

        // Auto Scaling answers newest first; the first page is enough for "the newest"
        // and for one refresh by id.
        var response = await _autoScaling.DescribeInstanceRefreshesAsync(request, ct);
        return (response.InstanceRefreshes ?? new List<InstanceRefresh>())
            .Select(r => new FleetRefresh(r.InstanceRefreshId, r.Status?.Value ?? "Unknown", r.PercentageComplete))
            .ToList();
    }

    private async Task<LaunchTemplateVersion?> DescribeVersionAsync(string template, string version, CancellationToken ct)
    {
        var request = new DescribeLaunchTemplateVersionsRequest { Versions = new List<string> { version } };
        if (IsTemplateId(template))
        {
            request.LaunchTemplateId = template;
        }
        else
        {
            request.LaunchTemplateName = template;
        }

        try
        {
            var response = await _ec2.DescribeLaunchTemplateVersionsAsync(request, ct);
            return response.LaunchTemplateVersions?.FirstOrDefault();
        }
        catch (AmazonEC2Exception ex) when (ex.ErrorCode is { } code && code.Contains("NotFound", StringComparison.Ordinal))
        {
            // InvalidLaunchTemplateName.NotFoundException, InvalidLaunchTemplateId.NotFound
            // and InvalidLaunchTemplateId.VersionNotFound all mean "no such thing".
            return null;
        }
    }

    private static bool IsTemplateId(string template) => template.StartsWith("lt-", StringComparison.Ordinal);

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
