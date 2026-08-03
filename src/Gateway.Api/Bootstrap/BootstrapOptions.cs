namespace Gateway.Api.Bootstrap;

/// <summary>
/// Tunables for the node-bootstrap pipeline (tech-spec §4.3). Bound from the
/// <c>Bootstrap</c> configuration section; <see cref="Enabled"/> is driven by the
/// <c>GATEWAY_BOOTSTRAP_ENABLED</c> environment variable so bootstrap is
/// <b>off by default</b> and only provisions a box when explicitly switched on —
/// the build/test environment never runs it.
/// </summary>
public sealed class BootstrapOptions
{
    /// <summary>Environment variable that gates the bootstrap pipeline; must equal <c>true</c> to enable.</summary>
    public const string EnabledEnvVar = "GATEWAY_BOOTSTRAP_ENABLED";

    /// <summary>Whether the bootstrap pipeline runs at startup. Off unless the env flag is set.</summary>
    public bool Enabled { get; set; }

    /// <summary>Internal Docker network downstream containers join (matches the reconciler's <c>app-net</c>).</summary>
    public string Network { get; set; } = "app-net";

    /// <summary>Path of the Docker daemon config file the log-rotation step manages.</summary>
    public string DockerDaemonConfigPath { get; set; } = "/etc/docker/daemon.json";

    /// <summary>Per-container log file size cap set in <c>daemon.json</c> (<c>log-opts.max-size</c>).</summary>
    public string DockerLogMaxSize { get; set; } = "10m";

    /// <summary>Number of rotated log files kept per container (<c>log-opts.max-file</c>).</summary>
    public string DockerLogMaxFile { get; set; } = "3";

    /// <summary>
    /// File in which the registry-login step records a fingerprint of the last
    /// credentials it applied, so a re-run with unchanged credentials skips the
    /// <c>docker login</c>. Never stores the credentials themselves — only a hash.
    /// </summary>
    public string RegistryAuthStatePath { get; set; } = "/var/lib/gateway-api/registry-auth.fingerprint";

    /// <summary>How often the registry credentials are refreshed (tech-spec §4.3: periodic re-login).</summary>
    public TimeSpan RegistryRefreshInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Path of the CloudWatch agent config file the metrics step manages.</summary>
    public string CloudWatchAgentConfigPath { get; set; } = "/opt/aws/amazon-cloudwatch-agent/etc/amazon-cloudwatch-agent.json";

    /// <summary>Path of the CloudWatch agent control script used to reload config.</summary>
    public string CloudWatchAgentCtlPath { get; set; } = "/opt/aws/amazon-cloudwatch-agent/bin/amazon-cloudwatch-agent-ctl";

    /// <summary>Log-group prefix for gateway/service logs (tech-spec §6, §9: <c>/gateway/{instance}</c>).</summary>
    public string LogGroupPrefix { get; set; } = "/gateway";

    /// <summary>Glob of gateway log files the CloudWatch agent ships.</summary>
    public string GatewayLogPath { get; set; } = "/var/log/gateway-api/*.log";

    /// <summary>CloudWatch metrics namespace for the node stats (disk/mem watermarks, tech-spec §9).</summary>
    public string MetricsNamespace { get; set; } = "Gateway";
}
