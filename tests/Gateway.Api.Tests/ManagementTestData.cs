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
        string tag = "latest",
        string? realtimeAuthPath = null,
        string? realtimeMessagePath = null,
        string? realtimeAllowedOrigins = null,
        bool? realtimePresence = null,
        string? realtimePublishToken = null) => new()
    {
        Name = name,
        Image = $"registry/{name}",
        Tag = tag,
        Digest = digest,
        Port = port,
        DesiredStatus = desiredStatus,
        IncludeInHealth = includeInHealth,
        RealtimeAuthPath = realtimeAuthPath,
        RealtimeMessagePath = realtimeMessagePath,
        RealtimeAllowedOrigins = realtimeAllowedOrigins,
        RealtimePresence = realtimePresence,
        RealtimePublishToken = realtimePublishToken,
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

    /// <summary>
    /// An <c>instance_status</c> row whose services JSON records a reconcile error for
    /// one service. When <paramref name="running"/> is true the service still has a
    /// running container (a failed upgrade); otherwise it is absent-but-desired (a
    /// failed start) and surfaced as an <c>absent</c> entry (task #553).
    /// </summary>
    public static InstanceStatus InstanceWithError(
        string instanceId,
        string service,
        string errorMessage,
        DateTimeOffset errorAt,
        bool running = false,
        string digest = "sha256:v1",
        DateTimeOffset? heartbeatAt = null)
    {
        var containers = running
            ? new List<ContainerInfo>
            {
                new(service, $"registry/{service}", digest, "running", DateTimeOffset.UnixEpoch, null, HostPort: 40000),
            }
            : new List<ContainerInfo>();

        var errors = new Dictionary<string, ServiceError>(StringComparer.Ordinal)
        {
            [service] = new ServiceError(errorMessage, errorAt),
        };

        return new InstanceStatus
        {
            InstanceId = instanceId,
            PrivateIp = "10.0.0.1",
            PublicIp = null,
            GatewayVer = "1.2.3",
            IsLeader = false,
            Services = InstanceServicesJson.Build(containers, errors),
            HeartbeatAt = heartbeatAt ?? DateTimeOffset.UtcNow,
        };
    }
}
