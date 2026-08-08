using Gateway.Api.Containers;
using Gateway.Api.Data;
using Gateway.Api.Instances;
using Gateway.Api.Management;

namespace Gateway.Api.Tests;

/// <summary>
/// Unit coverage for surfacing per-service reconcile errors through the fleet
/// (task #553): <see cref="InstanceServicesJson"/> round-trips lastError and keeps
/// old rows parseable, and <see cref="FleetRollup"/> counts erroring instances and
/// picks the most recent error.
/// </summary>
public class FleetErrorRollupTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static ContainerInfo Running(string name, string digest) =>
        new(name, $"registry/{name}", digest, "running", T0, null, HostPort: 40000);

    private static InstanceStatus Instance(string id, string servicesJson) => new()
    {
        InstanceId = id,
        PrivateIp = "10.0.0.1",
        GatewayVer = "1.0.0",
        Services = servicesJson,
        HeartbeatAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void ServicesJson_RoundTrips_LastError()
    {
        var errors = new Dictionary<string, ServiceError>(StringComparer.Ordinal)
        {
            ["svc-a"] = new ServiceError("boom", T0.AddSeconds(5)),
        };

        var json = InstanceServicesJson.Build(new[] { Running("svc-a", "sha256:v1") }, errors);
        var entry = Assert.Single(InstanceServicesJson.Parse(json));

        Assert.Equal("svc-a", entry.Name);
        Assert.Equal("running", entry.State);
        Assert.Equal("boom", entry.LastError);
        Assert.Equal(T0.AddSeconds(5), entry.LastErrorAt);
    }

    [Fact]
    public void ServicesJson_AbsentButErrored_EmitsAbsentEntry()
    {
        // A service with an error but no container gets a synthetic 'absent' entry.
        var errors = new Dictionary<string, ServiceError>(StringComparer.Ordinal)
        {
            ["svc-a"] = new ServiceError("No such image", T0.AddSeconds(1)),
        };

        var json = InstanceServicesJson.Build(Array.Empty<ContainerInfo>(), errors);
        var entry = Assert.Single(InstanceServicesJson.Parse(json));

        Assert.Equal("svc-a", entry.Name);
        Assert.Equal(InstanceServicesJson.AbsentState, entry.State);
        Assert.Null(entry.Digest);
        Assert.Null(entry.HostPort);
        Assert.Equal("No such image", entry.LastError);
    }

    [Fact]
    public void ServicesJson_OldRowWithoutErrorFields_ParsesFine()
    {
        // Backward compatibility (requirement #4): a row written before lastError/
        // lastErrorAt existed must still parse, with those fields defaulting to null.
        const string legacy =
            "[{\"name\":\"svc-a\",\"digest\":\"sha256:v1\",\"state\":\"running\"," +
            "\"startedAt\":\"1970-01-01T00:00:00+00:00\",\"restarts\":2,\"hostPort\":40001}]";

        var entry = Assert.Single(InstanceServicesJson.Parse(legacy));

        Assert.Equal("svc-a", entry.Name);
        Assert.Equal("sha256:v1", entry.Digest);
        Assert.Equal(2, entry.Restarts);
        Assert.Equal(40001, entry.HostPort);
        Assert.Null(entry.LastError);
        Assert.Null(entry.LastErrorAt);
    }

    [Fact]
    public void Rollup_CountsErroringInstances_AndPicksMostRecentError()
    {
        // svc-a: running-and-fine on i-1; running-but-failed-upgrade on i-2 (older
        // error); absent-and-failing on i-3 (newest error). errorOn = 2, latestError
        // = i-3's, and runningOn counts only the two with a running container.
        var okOnI1 = InstanceServicesJson.Build(new[] { Running("svc-a", "sha256:v2") });

        var i2Errors = new Dictionary<string, ServiceError>(StringComparer.Ordinal)
        {
            ["svc-a"] = new ServiceError("upgrade failed", T0.AddSeconds(10)),
        };
        var erroredOnI2 = InstanceServicesJson.Build(new[] { Running("svc-a", "sha256:v1") }, i2Errors);

        var i3Errors = new Dictionary<string, ServiceError>(StringComparer.Ordinal)
        {
            ["svc-a"] = new ServiceError("No such image", T0.AddSeconds(20)),
        };
        var absentOnI3 = InstanceServicesJson.Build(Array.Empty<ContainerInfo>(), i3Errors);

        var fleet = new[]
        {
            Instance("i-1", okOnI1),
            Instance("i-2", erroredOnI2),
            Instance("i-3", absentOnI3),
        };

        var rollup = FleetRollup.Compute("svc-a", fleet);

        Assert.Equal(2, rollup.RunningOn);          // i-1 and i-2 have running containers
        Assert.Equal(3, rollup.TotalInstances);
        Assert.Equal(2, rollup.ErrorOn);            // i-2 and i-3 carry an error
        Assert.NotNull(rollup.LatestError);
        Assert.Equal("i-3", rollup.LatestError!.InstanceId);
        Assert.Equal("No such image", rollup.LatestError.Message);
        Assert.Equal(T0.AddSeconds(20), rollup.LatestError.At);
    }

    [Fact]
    public void Rollup_NoErrors_ReportsZeroAndNullLatest()
    {
        var fleet = new[]
        {
            Instance("i-1", InstanceServicesJson.Build(new[] { Running("svc-a", "sha256:v1") })),
        };

        var rollup = FleetRollup.Compute("svc-a", fleet);

        Assert.Equal(0, rollup.ErrorOn);
        Assert.Null(rollup.LatestError);
    }
}
