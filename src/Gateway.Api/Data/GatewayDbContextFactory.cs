using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Gateway.Api.Data;

/// <summary>
/// Design-time factory used by the EF Core tools (`dotnet ef migrations …`).
/// The runtime app only wires up the context when GATEWAY_DB_CONNECTION is set,
/// so tooling needs its own way to build the context against the Npgsql provider
/// — migrations are authored for Postgres, which owns the schema (tech-spec §6).
/// The connection string here is never opened; it only selects the provider.
/// </summary>
public class GatewayDbContextFactory : IDesignTimeDbContextFactory<GatewayDbContext>
{
    public GatewayDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("GATEWAY_DB_CONNECTION")
            ?? "Host=localhost;Database=gateway;Username=gateway;Password=gateway";

        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(connection)
            .Options;

        return new GatewayDbContext(options);
    }
}
