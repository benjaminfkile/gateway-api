using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Data;

/// <summary>
/// EF Core context owning the desired-state / fleet-status schema (tech-spec §4.4).
/// Migrations own the schema; the manifest is mutated only by the Management API
/// and consumed by the reconcilers.
/// </summary>
public class GatewayDbContext : DbContext
{
    public GatewayDbContext(DbContextOptions<GatewayDbContext> options)
        : base(options)
    {
    }

    public DbSet<ServiceManifest> ServiceManifests => Set<ServiceManifest>();
    public DbSet<DeployHistory> DeployHistory => Set<DeployHistory>();
    public DbSet<InstanceStatus> InstanceStatus => Set<InstanceStatus>();
    public DbSet<DeployInstanceStatus> DeployInstanceStatus => Set<DeployInstanceStatus>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Postgres uses jsonb columns for the free-form payloads; every other
        // provider (e.g. Sqlite in tests) just stores them as text. The CLR
        // property is a string either way ("jsonb-as-string", per the spec).
        var jsonType = Database.IsNpgsql() ? "jsonb" : null;

        modelBuilder.Entity<ServiceManifest>(e =>
        {
            e.ToTable("service_manifest");
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Image).HasColumnName("image").IsRequired();
            e.Property(x => x.Tag).HasColumnName("tag").IsRequired();
            e.Property(x => x.Digest).HasColumnName("digest");
            e.Property(x => x.Port).HasColumnName("port");
            e.Property(x => x.DesiredStatus).HasColumnName("desired_status").IsRequired();
            e.Property(x => x.EnvSecretRef).HasColumnName("env_secret_ref");
            e.Property(x => x.RealtimePublishToken).HasColumnName("realtime_publish_token");
            e.Property(x => x.RealtimeAuthPath).HasColumnName("realtime_auth_path");
            e.Property(x => x.IncludeInHealth).HasColumnName("include_in_health");
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.RestartRequestedAt).HasColumnName("restart_requested_at");
        });

        modelBuilder.Entity<DeployHistory>(e =>
        {
            e.ToTable("deploy_history");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Service).HasColumnName("service").IsRequired();
            e.Property(x => x.FromDigest).HasColumnName("from_digest");
            e.Property(x => x.ToDigest).HasColumnName("to_digest");
            e.Property(x => x.Actor).HasColumnName("actor").IsRequired();
            e.Property(x => x.Action).HasColumnName("action").IsRequired();
            e.Property(x => x.Status).HasColumnName("status").IsRequired();
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.FinishedAt).HasColumnName("finished_at");
            e.Property(x => x.Detail).HasColumnName("detail").HasColumnType(jsonType);
        });

        modelBuilder.Entity<InstanceStatus>(e =>
        {
            e.ToTable("instance_status");
            e.HasKey(x => x.InstanceId);
            e.Property(x => x.InstanceId).HasColumnName("instance_id");
            e.Property(x => x.PrivateIp).HasColumnName("private_ip");
            e.Property(x => x.PublicIp).HasColumnName("public_ip");
            e.Property(x => x.GatewayVer).HasColumnName("gateway_ver");
            e.Property(x => x.IsLeader).HasColumnName("is_leader");
            e.Property(x => x.Services).HasColumnName("services").HasColumnType(jsonType);
            e.Property(x => x.HeartbeatAt).HasColumnName("heartbeat_at");
        });

        modelBuilder.Entity<DeployInstanceStatus>(e =>
        {
            e.ToTable("deploy_instance_status");
            e.HasKey(x => new { x.DeployId, x.InstanceId });
            e.Property(x => x.DeployId).HasColumnName("deploy_id");
            e.Property(x => x.InstanceId).HasColumnName("instance_id");
            e.Property(x => x.Status).HasColumnName("status").IsRequired();
            e.Property(x => x.Detail).HasColumnName("detail");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });
    }
}
