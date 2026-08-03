using Gateway.Api.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Desired-state / fleet-status store (tech-spec §4.4). The connection string
// comes from GATEWAY_DB_CONNECTION. When it is unset the gateway must still
// boot and serve /api/health — DB-backed features are simply inactive — so the
// context is only registered when a connection string is present.
var dbConnection = builder.Configuration["GATEWAY_DB_CONNECTION"]
    ?? Environment.GetEnvironmentVariable("GATEWAY_DB_CONNECTION");
if (!string.IsNullOrWhiteSpace(dbConnection))
{
    builder.Services.AddDbContext<GatewayDbContext>(options =>
        options.UseNpgsql(dbConnection));
}

var app = builder.Build();

app.MapGet("/api/health", () => Results.Json(new
{
    gateway = "up",
    timestamp = DateTime.UtcNow,
}));

app.Run();

public partial class Program { }
