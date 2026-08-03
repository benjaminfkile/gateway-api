using Gateway.Api.Data;
using Gateway.Api.Manifest;
using Gateway.Api.Proxy;
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

var app = builder.Build();

// WebSocket passthrough is native to YARP; enabling this ensures Upgrade
// requests flow through to downstream services untouched (tech-spec §4.1).
app.UseWebSockets();

app.MapGet("/api/health", () => Results.Json(new
{
    gateway = "up",
    timestamp = DateTime.UtcNow,
}));

// Application traffic is proxied with NO authentication of any kind (design
// invariant, tech-spec §1): no auth middleware sits in front of MapReverseProxy.
app.MapReverseProxy();

app.Run();

public partial class Program { }
