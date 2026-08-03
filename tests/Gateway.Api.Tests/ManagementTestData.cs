using Gateway.Api.Containers;
using Gateway.Api.Data;
using Gateway.Api.Instances;

namespace Gateway.Api.Tests;

/// <summary>Builders for seeding Management API test rows.</summary>
public static class ManagementTestData
{
    public static ServiceManifest Manifest(
        string name,
        int port = 8080,
        bool includeInHealth = true,
        string? digest = "sha256:v1",
        string desiredStatus = "running",
        string tag = "latest") => new()
    {
        Name = name,
        Image = $"registry/{name}",
        Tag = tag,
        Digest = digest,
        Port = port,
        DesiredStatus = desiredStatus,
        IncludeInHealth = includeInHealth,
        UpdatedBy = "seed",
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    /// <summary>An <c>instance_status</c> row whose <c>services</c> jsonb runs the given (service, digest) pairs.</summary>
    public static InstanceStatus Instance(
        string instanceId,
        bool isLeader = false,
        DateTimeOffset? heartbeatAt = null,
        params (string Service, string Digest)[] running)
    {
        var containers = running
            .Select(r => new ContainerInfo(r.Service, $"registry/{r.Service}", r.Digest, "running", DateTimeOffset.UnixEpoch, null))
            .ToList();

        return new InstanceStatus
        {
            InstanceId = instanceId,
            PrivateIp = "10.0.0.1",
            PublicIp = null,
            GatewayVer = "1.2.3",
            IsLeader = isLeader,
            Services = InstanceServicesJson.Build(containers),
            HeartbeatAt = heartbeatAt ?? DateTimeOffset.UtcNow,
        };
    }
}
