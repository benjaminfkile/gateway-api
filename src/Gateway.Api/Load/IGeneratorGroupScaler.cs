namespace Gateway.Api.Load;

/// <summary>The generator Auto Scaling group's sizes as <see cref="IGeneratorGroupScaler.DescribeAsync"/> reads them.</summary>
/// <param name="Desired">Desired capacity.</param>
/// <param name="Max">Maximum size.</param>
/// <param name="InService">Instances in the <c>InService</c> lifecycle state.</param>
public sealed record GeneratorGroupSize(int Desired, int Max, int InService);

/// <summary>
/// The Auto Scaling operations the load feature needs on the generator group
/// (docs/load.md sections 3, 5 and 6): read its desired, max and in-service counts,
/// and set its desired capacity. The gateway steers a group that already exists; it
/// never creates or deletes one, and it touches no other group.
/// <para>
/// Behind an interface so the build/test box, which has no AWS access, substitutes a
/// fake. <see cref="AwsGeneratorGroupScaler"/> is registered when
/// <c>Load:GeneratorGroup</c> is set, <see cref="NullGeneratorGroupScaler"/> otherwise;
/// every member of the null one throws <see cref="NotSupportedException"/>.
/// </para>
/// </summary>
public interface IGeneratorGroupScaler
{
    /// <summary>The named group's sizes, or null when no such group exists.</summary>
    Task<GeneratorGroupSize?> DescribeAsync(string group, CancellationToken ct = default);

    /// <summary>Set only the desired capacity of the named group.</summary>
    Task SetDesiredAsync(string group, int desired, CancellationToken ct = default);
}
