using Gateway.Api.Data;
using Gateway.Api.Health;
using Gateway.Api.Instances;
using Gateway.Api.Manifest;
using Gateway.Api.Proxy;
using Gateway.Api.Reconcile;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

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

    // Fleet awareness (tech-spec §4.3, §4.4): every reconcile loop upserts this
    // instance's status row via the EF store, and leadership — which gates the
    // stale-row cleanup — is a Postgres advisory lock on a dedicated connection.
    builder.Services.AddScoped<IInstanceStatusStore, EfInstanceStatusStore>();
    builder.Services.AddSingleton<ILeaderElection>(sp =>
        new PostgresAdvisoryLockLeaderElection(
            dbConnection,
            logger: sp.GetRequiredService<ILoggerFactory>()
                .CreateLogger<PostgresAdvisoryLockLeaderElection>()));
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

var app = builder.Build();

// WebSocket passthrough is native to YARP; enabling this ensures Upgrade
// requests flow through to downstream services untouched (tech-spec §4.1).
app.UseWebSockets();

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

// Application traffic is proxied with NO authentication of any kind (design
// invariant, tech-spec §1): no auth middleware sits in front of MapReverseProxy.
app.MapReverseProxy();

app.Run();

public partial class Program { }
