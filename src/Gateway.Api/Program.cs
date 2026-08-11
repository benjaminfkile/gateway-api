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
using Microsoft.AspNetCore.Cors.Infrastructure;
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
    // stale-row cleanup — is derived from those same heartbeats (no lock, no lease).
    builder.Services.AddScoped<IInstanceStatusStore, EfInstanceStatusStore>();
    // Heartbeat-derived leader election (tech-spec §4.3): the leader is simply the
    // lowest instance_id among instances whose heartbeat is within the stale
    // threshold. A hard-killed leader stops heartbeating, so its row ages out and a
    // successor takes over within ~1 reconcile loop — no zombie session can pin
    // leadership. The leader-only duties are idempotent, so a brief dual-leader
    // overlap during a transition is tolerated by design.
    builder.Services.AddSingleton<ILeaderElection>(sp =>
        new HeartbeatLeaderElection(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<InstanceMetadataProvider>(),
            sp.GetRequiredService<ReconcilerOptions>().InstanceStaleThreshold,
            sp.GetRequiredService<ILoggerFactory>()
                .CreateLogger<HeartbeatLeaderElection>()));

    // Apply pending EF Core migrations on boot (tech-spec §6). A fresh database
    // has no schema, so this must run before the reconciler/heartbeat or any
    // endpoint touches a table. The hosted service serializes the fleet with a
    // Postgres advisory lock (used only for migration; leader election uses none),
    // retries with backoff while the box waits for the DB, and fails fast (exits non-zero) if
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

// Cross-origin access for the ops dashboard (tech-spec §4.6): the SPA is served
// from its own origin, so browsers require CORS on /mgmt/* and the /hub
// negotiate. Only the origins in GATEWAY_CORS_ORIGINS (comma-separated) are
// allowed, with credentials for SignalR; unset → no CORS and the management
// plane stays same-origin-only. Proxied application traffic is never touched —
// downstream apps own their CORS end-to-end (design invariant, §1).
// The raw GATEWAY_CORS_ORIGINS value flows through ONE canonicalization
// (RealtimeAllowedOrigins) before feeding BOTH surfaces (task #607, origin parity):
// the static "ops" /mgmt policy and the dynamic /hub policy. Previously /mgmt used the
// verbatim Split() while /hub canonicalized, so the same env value — e.g. a trailing
// slash or an explicit :443 — could pass one surface and silently fail the other, and a
// malformed entry vanished from /hub with no diagnostic. Canonicalizing once removes
// that skew; the normalized/dropped entries are logged at Warning after the host builds.
var canonicalCors = RealtimeAllowedOrigins.CanonicalizeConfigured(
    Environment.GetEnvironmentVariable("GATEWAY_CORS_ORIGINS")
    ?? builder.Configuration["GATEWAY_CORS_ORIGINS"]);
var corsOrigins = canonicalCors
    .Where(o => !o.WasDropped)
    .Select(o => o.Canonical!)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();
// The static "ops" policy (dashboard origins) is registered only when configured and
// matches /mgmt exactly as before, save that its origins are now canonicalized. CORS
// services are always added, though, because /hub now carries a second, dynamic policy
// (task #595): the manifest-driven "hub" policy resolved by HubCorsPolicyProvider from
// HubCorsOriginCache. That policy's origin set is the static ops origins ∪ every
// service's realtime_allowed_origins, refreshed on a short TTL so a manifest CORS change
// needs no gateway restart and never runs a DB query per preflight.
builder.Services.AddCors(options =>
{
    if (corsOrigins.Length > 0)
    {
        options.AddPolicy("ops", policy => policy
            .WithOrigins(corsOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials());
    }
});
builder.Services.AddSingleton(sp => new HubCorsOriginCache(
    sp.GetRequiredService<ManifestSnapshotCache>(), corsOrigins));
builder.Services.AddSingleton<ICorsPolicyProvider, HubCorsPolicyProvider>();

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

// Surface every GATEWAY_CORS_ORIGINS entry the canonicalization above rewrote or
// dropped (task #607, origin parity): a normalized entry (before -> after) and a
// dropped entry (with the reason it failed) both used to be silent, so an operator
// could not tell why an origin that "looked right" never matched.
foreach (var entry in canonicalCors)
{
    if (entry.WasDropped)
    {
        app.Logger.LogWarning(
            "GATEWAY_CORS_ORIGINS entry '{Entry}' was dropped from both /mgmt and /hub CORS: {Reason}.",
            entry.Original,
            entry.DropReason);
    }
    else if (entry.WasNormalized)
    {
        app.Logger.LogWarning(
            "GATEWAY_CORS_ORIGINS entry '{Entry}' was normalized to '{Canonical}' for both /mgmt and /hub CORS.",
            entry.Original,
            entry.Canonical);
    }
}

// If GATEWAY_CORS_ORIGINS was SET but canonicalization dropped every entry, the "ops"
// policy above was silently not registered and /mgmt became same-origin-only with no
// browser-visible error (review finding). That total-loss mode deserves a louder
// signal than the per-entry warnings.
if (canonicalCors.Count > 0 && corsOrigins.Length == 0)
{
    app.Logger.LogError(
        "GATEWAY_CORS_ORIGINS was configured but every entry was dropped by canonicalization; "
        + "the 'ops' CORS policy is NOT registered and /mgmt will reject all cross-origin "
        + "browser requests. Fix the entries (absolute http(s) origins, no path/wildcard).");
}

// Keep the public and internal listeners' surfaces disjoint before any endpoint
// runs (tech-spec §8): /internal/* only on the internal port, nothing else there.
app.UseInternalListenerIsolation();

// WebSocket passthrough is native to YARP; enabling this ensures Upgrade
// requests flow through to downstream services untouched (tech-spec §4.1). The
// SignalR hub's WebSocket transport rides the same middleware.
app.UseWebSockets();

// CORS runs on the management/hub surface only, ahead of the plane guard so preflights
// succeed without a token. /mgmt keeps the strict static "ops" dashboard policy exactly
// as before (only when configured). /hub carries the dynamic, manifest-driven "hub"
// policy (task #595) — always active, since its allowed set includes consumer origins
// that exist independently of GATEWAY_CORS_ORIGINS; when nothing (static or manifest)
// is allowed it simply emits no CORS headers, matching the prior behaviour.
if (corsOrigins.Length > 0)
{
    app.UseWhen(
        ctx => ctx.Request.Path.StartsWithSegments("/mgmt"),
        branch => branch.UseCors("ops"));
}
app.UseWhen(
    ctx => ctx.Request.Path.StartsWithSegments("/hub"),
    branch => branch.UseCors(HubCorsPolicyProvider.PolicyName));

// Legacy-parity CORS for application traffic: the old gateway ran `cors()` —
// any origin, no credentials — in front of every proxied route, and the
// downstream apps rely on it (none terminate CORS themselves). Preflights are
// answered here because a downstream without an OPTIONS handler would 404
// them; response headers are only added when the downstream didn't set its
// own, so an app that owns its CORS is passed through untouched. /mgmt and
// /hub are excluded — they keep the stricter dashboard policy above.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    if (path.StartsWithSegments("/mgmt")
        || path.StartsWithSegments("/hub")
        || string.IsNullOrEmpty(ctx.Request.Headers.Origin))
    {
        await next();
        return;
    }

    if (HttpMethods.IsOptions(ctx.Request.Method)
        && !string.IsNullOrEmpty(ctx.Request.Headers.AccessControlRequestMethod))
    {
        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        ctx.Response.Headers.AccessControlAllowOrigin = "*";
        ctx.Response.Headers.AccessControlAllowMethods =
            ctx.Request.Headers.AccessControlRequestMethod;
        if (!string.IsNullOrEmpty(ctx.Request.Headers.AccessControlRequestHeaders))
        {
            ctx.Response.Headers.AccessControlAllowHeaders =
                ctx.Request.Headers.AccessControlRequestHeaders;
        }
        ctx.Response.Headers["Access-Control-Max-Age"] = "600";
        return;
    }

    ctx.Response.OnStarting(() =>
    {
        if (string.IsNullOrEmpty(ctx.Response.Headers.AccessControlAllowOrigin))
        {
            ctx.Response.Headers.AccessControlAllowOrigin = "*";
        }
        return Task.CompletedTask;
    });

    await next();
});

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
app.MapInternalPresence();

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
