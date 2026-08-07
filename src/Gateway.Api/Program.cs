using Gateway.Api.Auth;
using Gateway.Api.Bootstrap;
using Gateway.Api.Data;
using Gateway.Api.Health;
using Gateway.Api.Instances;
using Gateway.Api.Management;
using Gateway.Api.Manifest;
using Gateway.Api.Proxy;
using Gateway.Api.RealTime;
using Gateway.Api.Reconcile;
using Gateway.Api.Systemd;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Internal publish listener (tech-spec §4.2, §8): a second Kestrel endpoint bound
// from GATEWAY_INTERNAL_BIND, added alongside the public URLs. It hosts only
// POST /internal/publish and is never routed by the load balancer.
builder.AddGatewayInternalListener();

// Desired-state / fleet-status store (tech-spec §4.4). The connection string
// comes from GATEWAY_DB_CONNECTION. When it is set, the manifest is read from
// Postgres (EfManifestStore). When it is unset the gateway must still boot and
// serve traffic — DB-backed features are simply inactive — so we fall back to an
// in-memory manifest, seedable from the appsettings "Manifest" section for local
// dev without a database.
var dbConnection = builder.Configuration["GATEWAY_DB_CONNECTION"]
    ?? Environment.GetEnvironmentVariable("GATEWAY_DB_CONNECTION");
if (!string.IsNullOrWhiteSpace(dbConnection))
{
    builder.Services.AddDbContext<GatewayDbContext>(options =>
        options.UseNpgsql(dbConnection));
    builder.Services.AddScoped<IManifestStore, EfManifestStore>();

    // Deploy history + per-instance rollout progress (tech-spec §4.4, §4.5): the
    // Management API's audit/rollout ledger, EF-backed only when a database exists.
    builder.Services.AddScoped<IDeployStore, EfDeployStore>();

    // Fleet awareness (tech-spec §4.3, §4.4): every reconcile loop upserts this
    // instance's status row via the EF store, and leadership — which gates the
    // stale-row cleanup — is a Postgres advisory lock on a dedicated connection.
    builder.Services.AddScoped<IInstanceStatusStore, EfInstanceStatusStore>();
    builder.Services.AddSingleton<ILeaderElection>(sp =>
        new PostgresAdvisoryLockLeaderElection(
            dbConnection,
            logger: sp.GetRequiredService<ILoggerFactory>()
                .CreateLogger<PostgresAdvisoryLockLeaderElection>()));

    // Apply pending EF Core migrations on boot (tech-spec §6). A fresh database
    // has no schema, so this must run before the reconciler/heartbeat or any
    // endpoint touches a table. The hosted service serializes the fleet with a
    // Postgres advisory lock (distinct key from leader election), retries with
    // backoff while the box waits for the DB, and fails fast (exits non-zero) if
    // still unreachable — systemd's Restart=always then retries the whole boot.
    // Registered before AddNodeReconciler below so it starts first and opens the
    // MigrationReadinessGate the reconciler waits on.
    builder.Services.AddDatabaseMigration(dbConnection, builder.Configuration);
}
else
{
    var seed = builder.Configuration.GetSection("Manifest").Get<List<ServiceManifest>>()
        ?? new List<ServiceManifest>();
    builder.Services.AddSingleton<IManifestStore>(new InMemoryManifestStore(seed));
}

// Dynamic YARP edge proxy driven by the manifest (tech-spec §4.1). Routes are
// built in-memory and swapped at runtime via a change token — no restart.
builder.Services.AddManifestProxy();

// Aggregated health check (tech-spec §4.1): probes health-participating,
// running services in parallel behind IHealthProber and folds the results into
// the /api/health response below.
builder.Services.AddAggregatedHealth();

// Node reconciler (tech-spec §4.3, §7): converges the local box's containers
// toward the manifest with blue-green deploys. Off unless
// GATEWAY_RECONCILER_ENABLED=true, and the Docker-backed runtime only activates
// where the daemon is present — so the gateway boots cleanly without Docker.
builder.Services.AddNodeReconciler(builder.Configuration);

// Real-time hub + optional Redis backplane (tech-spec §4.2). SignalR groups back
// the {app}:{topic} channels; the backplane activates only when
// GATEWAY_REDIS_ENDPOINT is set, so single-node/dev/test boots without Redis.
builder.Services.AddGatewayRealtime(builder.Configuration);

// Management-plane auth (tech-spec §5): Cognito JWT bearer + ops authorization
// policies, configured from GATEWAY_COGNITO_AUTHORITY. Scoped strictly to /mgmt/*
// and the ops:* hub channels — application traffic is never authenticated. When
// the authority is unset the plane is unconfigured and /mgmt/* 503s (see guard
// below); the gateway still boots and serves all transparent traffic.
builder.Services.AddManagementAuthentication();

// Management API external-system seams (tech-spec §4.5): image registry (ECR),
// log store (CloudWatch), and the deploy-history store. AWS-backed impls are lazy
// so a region-less box still boots; the deploy store defaults to a no-op unless the
// DB branch above wired the EF-backed one.
builder.Services.AddManagementServices();

// Node bootstrap (tech-spec §4.3, §2): the idempotent pipeline that provisions the
// box (Docker daemon log rotation, internal network, registry login refreshed on a
// timer, CloudWatch agent config) — replacing hand-rolled user-data bash. Every
// host/process operation is behind ILinuxHost. Off unless GATEWAY_BOOTSTRAP_ENABLED
// =true, so the build/test box (no Docker, no AWS, no root FS) is never touched.
builder.Services.AddNodeBootstrap(builder.Configuration);

// systemd watchdog self-check (tech-spec §6): pings WATCHDOG=1 only while the health
// report keeps building. Inert unless run under systemd with the watchdog enabled.
builder.Services.AddSystemdWatchdog();

var app = builder.Build();

// Keep the public and internal listeners' surfaces disjoint before any endpoint
// runs (tech-spec §8): /internal/* only on the internal port, nothing else there.
app.UseInternalListenerIsolation();

// WebSocket passthrough is native to YARP; enabling this ensures Upgrade
// requests flow through to downstream services untouched (tech-spec §4.1). The
// SignalR hub's WebSocket transport rides the same middleware.
app.UseWebSockets();

// Management-plane configuration guard (tech-spec §5): /mgmt/* returns 503 when
// no Cognito authority is set. Runs before authentication so the closed-plane
// response never depends on a token; leaves proxy/health/hub traffic untouched.
app.UseManagementPlaneGuard();

// Authentication + authorization run for every request so the /mgmt endpoints and
// the ops:* hub channels can read a Cognito principal — but only endpoints that
// opt in via RequireAuthorization are gated. Nothing in front of the proxy or
// /api/health requires a token, preserving the transparency invariant (§1).
app.UseAuthentication();
app.UseAuthorization();

// Aggregated health: reports the gateway plus a per-service probe rollup. The
// gateway is always "up" here and the response is always 200 — a down service
// is surfaced under "services" but never fails the load balancer's own check
// (tech-spec §4.1). Shape is fixed for LB + ops v1 compatibility.
app.MapGet("/api/health", async (HealthAggregator aggregator, CancellationToken ct) =>
{
    var report = await aggregator.BuildAsync(ct);
    return Results.Json(new
    {
        gateway = report.Gateway,
        timestamp = report.Timestamp,
        services = report.Services.ToDictionary(
            kvp => kvp.Key,
            kvp => new
            {
                status = kvp.Value.Status,
                httpStatus = kvp.Value.HttpStatus,
                responseTimeMs = kvp.Value.ResponseTimeMs,
            }),
    });
});

// Real-time hub at /hub and the internal publish endpoint (tech-spec §4.2). The
// isolation middleware above ensures /internal/publish is reachable only on the
// internal listener.
app.MapGatewayHub();
app.MapInternalPublish();

// Management plane (tech-spec §5): the authenticated /mgmt surface. Each endpoint
// requires a valid Cognito access token; unauthenticated calls get 401 (or 503
// when the plane is unconfigured, via the guard above).
app.MapManagementPlane();

// The fleet-aware §4.5 Management endpoints (services, instances, deploys, logs,
// lifecycle, deploy/rollback). Each requires a Cognito access token and audit-logs
// mutating calls; all state is read from the DB, never local Docker.
app.MapManagementApi();

// Application traffic is proxied with NO authentication of any kind (design
// invariant, tech-spec §1): no auth middleware sits in front of MapReverseProxy.
app.MapReverseProxy();

app.Run();

public partial class Program { }
