using Gateway.Api.Containers;

namespace Gateway.Api.Reconcile;

/// <summary>
/// Tunables for the node reconciler (tech-spec §4.3, §7). Bound from the
/// <c>Reconciler</c> configuration section; <see cref="Enabled"/> is driven by the
/// <c>GATEWAY_RECONCILER_ENABLED</c> environment variable so the reconciler is
/// <b>off by default</b> and only mutates a box when explicitly switched on.
/// </summary>
public sealed class ReconcilerOptions
{
    /// <summary>Environment variable that gates the reconciler; must equal <c>true</c> to enable.</summary>
    public const string EnabledEnvVar = "GATEWAY_RECONCILER_ENABLED";

    /// <summary>Whether the reconcile loop runs. Off unless the env flag is set.</summary>
    public bool Enabled { get; set; }

    /// <summary>Internal Docker network downstream containers join.</summary>
    public string Network { get; set; } = "app-net";

    /// <summary>Base interval between reconcile loops (tech-spec: 30s).</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Upper bound of random jitter added to each interval so a large fleet does
    /// not hit the registry/DB in lockstep (tech-spec §4.3: up to 5s).
    /// </summary>
    public TimeSpan MaxJitter { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Health path polled on a blue-green candidate (tech-spec §7 default: <c>/api/health</c>).</summary>
    public string HealthPath { get; set; } = "/api/health";

    /// <summary>How long to wait for a green candidate to become ready (tech-spec: 60s).</summary>
    public TimeSpan ReadinessTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Delay between readiness polls.</summary>
    public TimeSpan ReadinessPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Drain delay after cutting traffic to green before removing the old container (tech-spec: 30s).</summary>
    public TimeSpan DrainDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Log driver applied to every container the reconciler starts (awslogs in production).</summary>
    public LogDriverConfig LogDriver { get; set; } = new();

    /// <summary>
    /// TTL for the per-ref Secrets Manager environment cache (tech-spec §8). Resolved
    /// env is cached this long so a 30s fleet loop does not hit Secrets Manager per
    /// service per loop, while a rotated secret still lands within ~TTL + one loop. A
    /// failed fetch is never cached. Default 60s.
    /// </summary>
    public TimeSpan SecretCacheTtl { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Age past which an <c>instance_status</c> row is considered a departed
    /// instance and pruned by the leader (tech-spec §4.4: rows stale &gt; 90s).
    /// </summary>
    public TimeSpan InstanceStaleThreshold { get; set; } = TimeSpan.FromSeconds(90);
}
