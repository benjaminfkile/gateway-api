namespace Gateway.Api.Instances;

/// <summary>
/// Identity of the box this gateway runs on (tech-spec §4.4 <c>instance_status</c>):
/// a stable id plus the private/public IPs used by the fleet-status heartbeat.
/// </summary>
/// <param name="InstanceId">Stable instance identifier (e.g. an EC2 instance id). Primary key of <c>instance_status</c>.</param>
/// <param name="PrivateIp">Private (in-VPC) IP, or null when unknown.</param>
/// <param name="PublicIp">Public IP, or null when the instance has none.</param>
public sealed record InstanceIdentity(string InstanceId, string? PrivateIp, string? PublicIp);

/// <summary>
/// A source of this instance's <see cref="InstanceIdentity"/> (tech-spec §4.4).
/// The concrete sources are <see cref="Ec2InstanceMetadata"/> (IMDSv2) and
/// <see cref="EnvInstanceMetadata"/> (environment fallback); an
/// <see cref="InstanceMetadataProvider"/> selects between them at runtime, falling
/// back to the environment when IMDS is unreachable.
/// <para>
/// Every source is behind this interface so the reconciler is testable without
/// EC2 or a network — the build/test box has neither.
/// </para>
/// </summary>
public interface IInstanceMetadata
{
    /// <summary>
    /// Resolve this instance's identity from this source, or <c>null</c> when the
    /// source does not apply here (e.g. IMDS is unreachable off-EC2). A null result
    /// signals the <see cref="InstanceMetadataProvider"/> to try the next source.
    /// </summary>
    Task<InstanceIdentity?> TryResolveAsync(CancellationToken ct = default);
}
