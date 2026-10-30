using Amazon.AutoScaling;
using Amazon.AutoScaling.Model;

namespace Gateway.Api.Load;

/// <summary>
/// <see cref="IGeneratorGroupScaler"/> over Amazon EC2 Auto Scaling (AWSSDK.AutoScaling).
/// The client comes from the container: default credential chain, region from the
/// environment. An unknown group reads as null; every other AWS error is thrown to the
/// caller.
/// </summary>
public sealed class AwsGeneratorGroupScaler : IGeneratorGroupScaler
{
    private readonly IAmazonAutoScaling _autoScaling;

    public AwsGeneratorGroupScaler(IAmazonAutoScaling autoScaling)
    {
        _autoScaling = autoScaling;
    }

    public async Task<GeneratorGroupSize?> DescribeAsync(string group, CancellationToken ct = default)
    {
        var response = await _autoScaling.DescribeAutoScalingGroupsAsync(new DescribeAutoScalingGroupsRequest
        {
            AutoScalingGroupNames = new List<string> { group },
        }, ct);

        var found = response.AutoScalingGroups?.FirstOrDefault(g =>
            string.Equals(g.AutoScalingGroupName, group, StringComparison.Ordinal));
        if (found is null)
        {
            return null;
        }

        var inService = found.Instances?.Count(i =>
            string.Equals(i.LifecycleState?.Value, LifecycleState.InService.Value, StringComparison.Ordinal)) ?? 0;
        return new GeneratorGroupSize(found.DesiredCapacity, found.MaxSize, inService);
    }

    public Task SetDesiredAsync(string group, int desired, CancellationToken ct = default) =>
        _autoScaling.SetDesiredCapacityAsync(new SetDesiredCapacityRequest
        {
            AutoScalingGroupName = group,
            DesiredCapacity = desired,
            HonorCooldown = false,
        }, ct);
}
