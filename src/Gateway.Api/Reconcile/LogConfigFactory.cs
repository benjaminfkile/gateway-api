using Gateway.Api.Containers;
using Gateway.Api.Management;

namespace Gateway.Api.Reconcile;

/// <summary>
/// Builds the per-container <see cref="LogDriverConfig"/> the reconciler applies
/// when it (re)creates a service container (tech-spec §4.3, §9). Unlike a single
/// static config, the log group and stream are derived <b>per service</b> so every
/// container ships to CloudWatch group <c>/gateway/services/{service}</c> with one
/// stream per instance — the shape the dashboard log viewer reads back through
/// <see cref="CloudWatchLogStore"/>.
/// <para>
/// The group template is taken from <see cref="CloudWatchLogStore.LogGroupFor"/> so
/// the write path (awslogs driver) and the read path can never diverge. A
/// blue-green candidate is started with its <b>service's</b> group/stream (not the
/// <c>-green</c> container name) so its logs land in the canonical service group and
/// survive the promotion.
/// </para>
/// </summary>
public static class LogConfigFactory
{
    /// <summary>Centralized CloudWatch log driver (production default).</summary>
    public const string AwsLogsDriver = "awslogs";

    /// <summary>Local rotating-file driver (dev escape hatch).</summary>
    public const string JsonFileDriver = "json-file";

    /// <summary>Env var forcing the local <c>json-file</c> escape hatch for a box without AWS.</summary>
    public const string DriverEnvVar = "GATEWAY_LOG_DRIVER";

    /// <summary>Primary source of the awslogs region.</summary>
    public const string RegionEnvVar = "AWS_REGION";

    /// <summary>Secondary source of the awslogs region (standard AWS SDK fallback).</summary>
    public const string FallbackRegionEnvVar = "AWS_DEFAULT_REGION";

    /// <summary>Retention applied to every awslogs-created group (tech-spec §9).</summary>
    public const int RetentionDays = 30;

    /// <summary>Whether the effective driver ships to CloudWatch (vs the local escape hatch).</summary>
    public static bool UsesAwsLogs(LogDriverConfig configured) => !IsJsonFile(configured.Driver);

    private static bool IsJsonFile(string? driver) =>
        string.Equals(driver, JsonFileDriver, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Build the log config for a single service container. <paramref name="service"/>
    /// is always the canonical service name (a green candidate uses its service's
    /// group/stream, not the <c>-green</c> container name).
    /// </summary>
    public static LogDriverConfig ForService(
        string service,
        string instanceId,
        string? instanceRegion,
        LogDriverConfig configured)
    {
        // Escape hatch (tech-spec §4.3): a dev box without AWS forces json-file with
        // sane rotation so container logs stay bounded on local disk. Selected when
        // GATEWAY_LOG_DRIVER=json-file or Reconciler:LogDriver:Driver=json-file.
        if (IsJsonFile(configured.Driver))
        {
            return new LogDriverConfig
            {
                Driver = JsonFileDriver,
                Options =
                {
                    ["max-size"] = "10m",
                    ["max-file"] = "3",
                },
            };
        }

        // awslogs → CloudWatch group /gateway/services/{service}, stream = instance id.
        // Any operator-configured options are preserved (e.g. a custom endpoint); the
        // canonical keys below always win so per-service naming is authoritative.
        var options = new Dictionary<string, string>(configured.Options, StringComparer.Ordinal)
        {
            ["awslogs-group"] = CloudWatchLogStore.LogGroupFor(service),
            ["awslogs-stream"] = instanceId,
            // awslogs-create-group makes the group on first write; retention is set
            // separately (it cannot) via ILogGroupAdmin — see tech-spec §9.
            ["awslogs-create-group"] = "true",
        };

        var region = ResolveRegion(instanceRegion);
        if (!string.IsNullOrEmpty(region))
        {
            options["awslogs-region"] = region;
        }

        return new LogDriverConfig { Driver = AwsLogsDriver, Options = options };
    }

    /// <summary>
    /// Resolve the awslogs region: <c>AWS_REGION</c> → <c>AWS_DEFAULT_REGION</c> →
    /// the instance's own region (IMDS). Null when none is known — the driver then
    /// falls back to the agent/host default.
    /// </summary>
    public static string? ResolveRegion(string? instanceRegion)
    {
        return Trimmed(Environment.GetEnvironmentVariable(RegionEnvVar))
            ?? Trimmed(Environment.GetEnvironmentVariable(FallbackRegionEnvVar))
            ?? Trimmed(instanceRegion);
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
