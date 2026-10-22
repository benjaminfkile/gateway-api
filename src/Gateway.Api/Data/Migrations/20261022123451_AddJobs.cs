using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Gateway.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "job",
                columns: table => new
                {
                    name = table.Column<string>(type: "text", nullable: false),
                    schedule = table.Column<string>(type: "text", nullable: true),
                    time_zone = table.Column<string>(type: "text", nullable: false),
                    script = table.Column<string>(type: "text", nullable: false),
                    script_version = table.Column<int>(type: "integer", nullable: false),
                    image = table.Column<string>(type: "text", nullable: false),
                    tag = table.Column<string>(type: "text", nullable: false),
                    env_secret_ref = table.Column<string>(type: "text", nullable: true),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    next_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "job_run",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    job = table.Column<string>(type: "text", nullable: false),
                    trigger = table.Column<string>(type: "text", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    instance_id = table.Column<string>(type: "text", nullable: true),
                    image_digest = table.Column<string>(type: "text", nullable: true),
                    script_version = table.Column<int>(type: "integer", nullable: false),
                    exit_code = table.Column<int>(type: "integer", nullable: true),
                    queued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    detail = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_run", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "job_script_version",
                columns: table => new
                {
                    job = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    script = table.Column<string>(type: "text", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_script_version", x => new { x.job, x.version });
                });

            migrationBuilder.CreateIndex(
                name: "IX_job_run_job_id",
                table: "job_run",
                columns: new[] { "job", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_job_run_status",
                table: "job_run",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_job_run_active",
                table: "job_run",
                column: "job",
                unique: true,
                filter: "status IN ('queued', 'running')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "job");

            migrationBuilder.DropTable(
                name: "job_run");

            migrationBuilder.DropTable(
                name: "job_script_version");
        }
    }
}
