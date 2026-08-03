using System.Net.Http.Headers;
using Gateway.Api.Auth;
using Gateway.Api.Data;
using Gateway.Api.Instances;
using Gateway.Api.Management;
using Gateway.Api.Manifest;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Gateway.Api.Tests;

/// <summary>
/// Test host for the fleet-aware Management API (tech-spec §4.5): a real
/// <see cref="Program"/> pipeline with the Cognito authority set (plane configured),
/// backed by Sqlite for the manifest / instance-status / deploy-history stores and
/// fakes for the AWS seams (image registry, log store). Tokens are locally signed
/// (see <see cref="TestManagementAuth"/>) so the authz matrix is exercised without a
/// network JWKS fetch.
/// </summary>
public sealed class ManagementApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public FakeImageRegistry ImageRegistry { get; } = new();
    public FakeLogStore LogStore { get; } = new();

    public ManagementApiFactory()
    {
        _connection.Open();

        // Create the schema up front on the shared connection so the tables exist
        // before the host starts — the proxy route initializer queries the manifest
        // at startup, ahead of any per-request scope.
        using var db = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options);
        db.Database.EnsureCreated();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Configure the plane so /mgmt/* is reachable (not 503).
                [ManagementAuthOptions.AuthorityEnvVar] = TestManagementAuth.Issuer,
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Sqlite stands in for Postgres (the build box has neither). The migration
            // is authored for Npgsql, so ignore the model-difference warning as the
            // data-layer tests do.
            services.AddDbContext<GatewayDbContext>(options => options
                .UseSqlite(_connection)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

            services.RemoveAll<IManifestStore>();
            services.AddScoped<IManifestStore, EfManifestStore>();

            services.RemoveAll<IInstanceStatusStore>();
            services.AddScoped<IInstanceStatusStore, EfInstanceStatusStore>();

            services.RemoveAll<IDeployStore>();
            services.AddScoped<IDeployStore, EfDeployStore>();

            services.RemoveAll<IImageRegistry>();
            services.AddSingleton<IImageRegistry>(ImageRegistry);

            services.RemoveAll<ILogStore>();
            services.AddSingleton<ILogStore>(LogStore);

            // Trust the local test signing key instead of Cognito's JWKS — no network.
            services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                TestManagementAuth.ApplyTestSigningKey);
        });
    }

    /// <summary>Run an action against a fresh DbContext scope (seed rows, assert state).</summary>
    public async Task WithDbAsync(Func<GatewayDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        await action(db);
    }

    /// <summary>An HTTP client whose requests carry the given bearer token by default.</summary>
    public HttpClient CreateClient(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
        }
    }

    /// <summary>Convenience token minters for the authz matrix.</summary>
    public static string AdminToken(string username = "alice-admin") =>
        TestManagementAuth.CreateToken(groups: new[] { ManagementAuth.OpsAdminGroup }, username: username);

    public static string DeployToken(string username = "bob-deploy") =>
        TestManagementAuth.CreateToken(groups: new[] { ManagementAuth.OpsDeployGroup }, username: username);

    public static string CiToken() =>
        TestManagementAuth.CreateToken(scopes: new[] { ManagementAuth.DeployScope }, username: "ci");

    public static string NoRoleToken() => TestManagementAuth.CreateToken();
}
