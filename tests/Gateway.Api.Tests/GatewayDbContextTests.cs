using Gateway.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Gateway.Api.Tests;

/// <summary>
/// Data-layer tests. The build/test box has no Postgres, so these exercise the
/// model and the migration against Sqlite in-memory (jsonb/timestamptz map to
/// Sqlite affinities transparently since the payloads are "jsonb-as-string").
/// </summary>
public class GatewayDbContextTests
{
    private static (GatewayDbContext ctx, SqliteConnection conn) NewContext()
    {
        // A shared open connection keeps the in-memory database alive for the
        // lifetime of the test.
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();

        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite(conn)
            // The migration is authored for Npgsql (jsonb/timestamptz); replaying
            // it through the Sqlite provider naturally reports model differences.
            // We only assert the migration *applies*, so ignore that warning.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        return (new GatewayDbContext(options), conn);
    }

    [Fact]
    public void Model_RoundTrips_AllEntities()
    {
        var (ctx, conn) = NewContext();
        using (conn)
        using (ctx)
        {
            ctx.Database.EnsureCreated();

            ctx.ServiceManifests.Add(new ServiceManifest
            {
                Name = "svc-a",
                Image = "registry/svc-a",
                Tag = "latest",
                Digest = "sha256:abc",
                Port = 8081,
                DesiredStatus = "running",
                EnvSecretRef = "secret/svc-a",
                IncludeInHealth = true,
                UpdatedBy = "ops-admin",
                UpdatedAt = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero),
            });

            var deploy = new DeployHistory
            {
                Service = "svc-a",
                FromDigest = null,
                ToDigest = "sha256:abc",
                Actor = "ops-admin",
                Action = "deploy",
                Status = "complete",
                StartedAt = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero),
                FinishedAt = new DateTimeOffset(2026, 8, 3, 12, 1, 0, TimeSpan.Zero),
                Detail = "{\"note\":\"first deploy\"}",
            };
            ctx.DeployHistory.Add(deploy);

            ctx.InstanceStatus.Add(new InstanceStatus
            {
                InstanceId = "i-123",
                PrivateIp = "10.0.0.1",
                PublicIp = null,
                GatewayVer = "1.0.0",
                IsLeader = true,
                Services = "[{\"name\":\"svc-a\",\"digest\":\"sha256:abc\"}]",
                HeartbeatAt = new DateTimeOffset(2026, 8, 3, 12, 2, 0, TimeSpan.Zero),
            });

            ctx.SaveChanges();

            ctx.DeployInstanceStatus.Add(new DeployInstanceStatus
            {
                DeployId = deploy.Id,
                InstanceId = "i-123",
                Status = "converged",
                Detail = null,
                UpdatedAt = new DateTimeOffset(2026, 8, 3, 12, 3, 0, TimeSpan.Zero),
            });
            ctx.SaveChanges();
            ctx.ChangeTracker.Clear();

            var manifest = ctx.ServiceManifests.Single(x => x.Name == "svc-a");
            Assert.Equal("registry/svc-a", manifest.Image);
            Assert.Equal(8081, manifest.Port);
            Assert.True(manifest.IncludeInHealth);
            Assert.Equal("secret/svc-a", manifest.EnvSecretRef);
            Assert.Equal(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero), manifest.UpdatedAt);

            var savedDeploy = ctx.DeployHistory.Single();
            Assert.True(savedDeploy.Id > 0); // surrogate key was generated
            Assert.Null(savedDeploy.FromDigest);
            Assert.Equal("{\"note\":\"first deploy\"}", savedDeploy.Detail);
            Assert.NotNull(savedDeploy.FinishedAt);

            var instance = ctx.InstanceStatus.Single();
            Assert.True(instance.IsLeader);
            Assert.Contains("svc-a", instance.Services);

            var progress = ctx.DeployInstanceStatus.Single();
            Assert.Equal(deploy.Id, progress.DeployId);
            Assert.Equal("i-123", progress.InstanceId);
            Assert.Equal("converged", progress.Status);
        }
    }

    [Fact]
    public void Migration_Applies_OnSqlite()
    {
        var (ctx, conn) = NewContext();
        using (conn)
        using (ctx)
        {
            // Replays the committed Npgsql migration through the Sqlite provider.
            ctx.Database.Migrate();

            var applied = ctx.Database.GetAppliedMigrations().ToList();
            Assert.Contains(applied, m => m.EndsWith("_InitialCreate"));

            // Every table from the migration is usable after it applies.
            ctx.ServiceManifests.Add(new ServiceManifest
            {
                Name = "svc-b",
                Image = "registry/svc-b",
                Tag = "v1",
                Port = 8082,
                DesiredStatus = "stopped",
                IncludeInHealth = false,
                UpdatedBy = "ops-admin",
                UpdatedAt = DateTimeOffset.UnixEpoch,
            });
            ctx.SaveChanges();

            Assert.Equal(1, ctx.ServiceManifests.Count());
        }
    }
}
