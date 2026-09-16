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

    /// <summary>
    /// Environment variable overriding <see cref="DeployTimeout"/>, expressed as a
    /// whole number of seconds (e.g. <c>600</c> for ten minutes).
    /// </summary>
    public const string DeployTimeoutEnvVar = "GATEWAY_DEPLOY_TIMEOUT";

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

    /// <summary>
    /// How long an <c>in_progress</c> deploy may run before the leader gives up on it
    /// (tech-spec §4.5, §7). A deploy older than this whose fleet has not fully
    /// converged is marked <c>failed</c> (or <c>partial</c> if some instances did
    /// converge) with error "deploy timed out" — so a stuck rollout stops being
    /// rescanned every loop instead of sitting <c>in_progress</c> forever. Default
    /// 10 minutes; override with <see cref="DeployTimeoutEnvVar"/>.
    /// </summary>
    public TimeSpan DeployTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Whether the image-prune housekeeping pass runs at the end of each reconcile
    /// loop (tech-spec §4.3 Housekeeping). Every instance prunes its own root
    /// volume — this is not a leader-only duty because disk is per box. Default true.
    /// </summary>
    public bool ImagePruneEnabled { get; set; } = true;

    /// <summary>
    /// Minimum interval between image-prune runs on this instance. The reconcile
    /// loop ticks every <see cref="Interval"/>, but scanning the daemon's image list
    /// each pass is wasted work — one prune per hour keeps the root volume flat
    /// without adding load. The first pass after startup may run immediately.
    /// Default 1 hour.
    /// </summary>
    public TimeSpan ImagePruneInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Age grace period passed to <see cref="Containers.IContainerRuntime.PruneImagesAsync"/>:
    /// images younger than this are always kept. Matches the tech-spec's
    /// "prune untagged images older than 48h". Default 48 hours.
    /// </summary>
    public TimeSpan ImagePruneAge { get; set; } = TimeSpan.FromHours(48);

    /// <summary>
    /// The N most-recently-created images to keep per repository, passed through to
    /// <see cref="Containers.IContainerRuntime.PruneImagesAsync"/>. Pure age is too
    /// weak at this fleet's deploy cadence: a busy service is deployed many times a
    /// day, so a 48h age floor alone still parks 20-30 stale images for it. Keeping
    /// the N most recent per repository caps steady-state disk regardless of how
    /// hard the fleet is deployed to. Default 3.
    /// </summary>
    public int ImagePruneKeep { get; set; } = 3;
}
