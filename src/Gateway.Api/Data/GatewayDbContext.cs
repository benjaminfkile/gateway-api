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
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobScriptVersion> JobScriptVersions => Set<JobScriptVersion>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();
    public DbSet<LoadScenario> LoadScenarios => Set<LoadScenario>();
    public DbSet<LoadScenarioVersion> LoadScenarioVersions => Set<LoadScenarioVersion>();
    public DbSet<LoadRun> LoadRuns => Set<LoadRun>();
    public DbSet<LoadRunStat> LoadRunStats => Set<LoadRunStat>();
    public DbSet<ResourceAudit> ResourceAudit => Set<ResourceAudit>();

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
            e.Property(x => x.RealtimeMessagePath).HasColumnName("realtime_message_path");
            e.Property(x => x.RealtimeAllowedOrigins).HasColumnName("realtime_allowed_origins");
            e.Property(x => x.RealtimePresence).HasColumnName("realtime_presence");
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
            e.Property(x => x.Cpu).HasColumnName("cpu");
            e.Property(x => x.Memory).HasColumnName("memory");
            e.Property(x => x.Disk).HasColumnName("disk");
            e.Property(x => x.HubConnections).HasColumnName("hub_connections");
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

        // Scheduled jobs (docs/jobs.md section 2).
        modelBuilder.Entity<Job>(e =>
        {
            e.ToTable("job");
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Schedule).HasColumnName("schedule");
            e.Property(x => x.TimeZone).HasColumnName("time_zone").IsRequired();
            e.Property(x => x.Script).HasColumnName("script").IsRequired();
            e.Property(x => x.ScriptVersion).HasColumnName("script_version");
            e.Property(x => x.Image).HasColumnName("image").IsRequired();
            e.Property(x => x.Tag).HasColumnName("tag").IsRequired();
            e.Property(x => x.EnvSecretRef).HasColumnName("env_secret_ref");
            e.Property(x => x.TimeoutSeconds).HasColumnName("timeout_seconds");
            e.Property(x => x.Enabled).HasColumnName("enabled");
            e.Property(x => x.NextRunAt).HasColumnName("next_run_at");
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<JobScriptVersion>(e =>
        {
            e.ToTable("job_script_version");
            e.HasKey(x => new { x.Job, x.Version });
            e.Property(x => x.Job).HasColumnName("job");
            e.Property(x => x.Version).HasColumnName("version");
            e.Property(x => x.Script).HasColumnName("script").IsRequired();
            e.Property(x => x.Sha256).HasColumnName("sha256").IsRequired();
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<JobRun>(e =>
        {
            e.ToTable("job_run");
            e.HasKey(x => x.Id);
            // job is deliberately not a foreign key: run history outlives a deleted job.
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.Job).HasColumnName("job").IsRequired();
            e.Property(x => x.Trigger).HasColumnName("trigger").IsRequired();
            e.Property(x => x.Actor).HasColumnName("actor").IsRequired();
            e.Property(x => x.Status).HasColumnName("status").IsRequired();
            e.Property(x => x.InstanceId).HasColumnName("instance_id");
            e.Property(x => x.ImageDigest).HasColumnName("image_digest");
            e.Property(x => x.ScriptVersion).HasColumnName("script_version");
            e.Property(x => x.ExitCode).HasColumnName("exit_code");
            e.Property(x => x.QueuedAt).HasColumnName("queued_at");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.FinishedAt).HasColumnName("finished_at");
            e.Property(x => x.CancelRequestedAt).HasColumnName("cancel_requested_at");
            e.Property(x => x.Detail).HasColumnName("detail");
            e.HasIndex(x => new { x.Job, x.Id });
            e.HasIndex(x => x.Status);
            // One run at a time per job, enforced by the database so two concurrent
            // enqueues (or a manual run racing a scheduled claim) cannot both land.
            e.HasIndex(x => x.Job)
                .IsUnique()
                .HasFilter("status IN ('queued', 'running')")
                .HasDatabaseName("ux_job_run_active");
        });

        // Load runs (docs/load.md section 2).
        modelBuilder.Entity<LoadScenario>(e =>
        {
            e.ToTable("load_scenario");
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Repository).HasColumnName("repository").IsRequired();
            e.Property(x => x.CommitSha).HasColumnName("commit_sha");
            e.Property(x => x.Script).HasColumnName("script").IsRequired();
            e.Property(x => x.ScriptVersion).HasColumnName("script_version");
            e.Property(x => x.Image).HasColumnName("image").IsRequired();
            e.Property(x => x.Tag).HasColumnName("tag").IsRequired();
            e.Property(x => x.ParamsDefault).HasColumnName("params_default").IsRequired();
            e.Property(x => x.MaxVusPerGenerator).HasColumnName("max_vus_per_generator");
            e.Property(x => x.LastValidationRunId).HasColumnName("last_validation_run_id");
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<LoadScenarioVersion>(e =>
        {
            e.ToTable("load_scenario_version");
            e.HasKey(x => new { x.Scenario, x.Version });
            e.Property(x => x.Scenario).HasColumnName("scenario");
            e.Property(x => x.Version).HasColumnName("version");
            e.Property(x => x.Script).HasColumnName("script").IsRequired();
            e.Property(x => x.Sha256).HasColumnName("sha256").IsRequired();
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<LoadRun>(e =>
        {
            e.ToTable("load_run");
            e.HasKey(x => x.Id);
            // scenario is deliberately not a foreign key: run history outlives a deleted scenario.
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.Scenario).HasColumnName("scenario").IsRequired();
            e.Property(x => x.ScenarioVersion).HasColumnName("scenario_version");
            e.Property(x => x.Target).HasColumnName("target").IsRequired();
            e.Property(x => x.Validation).HasColumnName("validation");
            e.Property(x => x.Actor).HasColumnName("actor").IsRequired();
            e.Property(x => x.Status).HasColumnName("status").IsRequired();
            e.Property(x => x.Generators).HasColumnName("generators");
            e.Property(x => x.VusPerGenerator).HasColumnName("vus_per_generator");
            e.Property(x => x.RampSeconds).HasColumnName("ramp_seconds");
            e.Property(x => x.HoldSeconds).HasColumnName("hold_seconds");
            e.Property(x => x.Params).HasColumnName("params").IsRequired();
            e.Property(x => x.HardStopAt).HasColumnName("hard_stop_at");
            e.Property(x => x.TokenHash).HasColumnName("token_hash").IsRequired();
            e.Property(x => x.SpecToken).HasColumnName("spec_token");
            e.Property(x => x.CheckedIn).HasColumnName("checked_in");
            e.Property(x => x.Checkins).HasColumnName("checkins").IsRequired().HasDefaultValue("{}");
            e.Property(x => x.QueuedAt).HasColumnName("queued_at");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.RampStartedAt).HasColumnName("ramp_started_at");
            e.Property(x => x.FinishedAt).HasColumnName("finished_at");
            e.Property(x => x.StopRequestedAt).HasColumnName("stop_requested_at");
            e.Property(x => x.StopReason).HasColumnName("stop_reason");
            e.Property(x => x.Verdict).HasColumnName("verdict");
            e.Property(x => x.Detail).HasColumnName("detail");
            e.Property(x => x.Peaks).HasColumnName("peaks").IsRequired();
            e.Property(x => x.Timeline).HasColumnName("timeline").IsRequired();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => new { x.Scenario, x.Id });
        });

        modelBuilder.Entity<LoadRunStat>(e =>
        {
            e.ToTable("load_run_stat");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.RunId).HasColumnName("run_id");
            e.Property(x => x.Source).HasColumnName("source").IsRequired();
            e.Property(x => x.Index).HasColumnName("index");
            e.Property(x => x.At).HasColumnName("at");
            e.Property(x => x.Body).HasColumnName("body").IsRequired();
            e.HasIndex(x => new { x.RunId, x.At });
        });

        modelBuilder.Entity<ResourceAudit>(e =>
        {
            e.ToTable("resource_audit");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.At).HasColumnName("at");
            e.Property(x => x.Actor).HasColumnName("actor").IsRequired();
            e.Property(x => x.Resource).HasColumnName("resource").IsRequired();
            e.Property(x => x.Action).HasColumnName("action").IsRequired();
            e.Property(x => x.Before).HasColumnName("before");
            e.Property(x => x.After).HasColumnName("after");
            e.Property(x => x.Detail).HasColumnName("detail");
            e.HasIndex(x => new { x.Resource, x.Id });
        });
    }
}
