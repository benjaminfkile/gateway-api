using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Gateway.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLoad : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "load_run",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    scenario = table.Column<string>(type: "text", nullable: false),
                    scenario_version = table.Column<int>(type: "integer", nullable: false),
                    target = table.Column<string>(type: "text", nullable: false),
                    validation = table.Column<bool>(type: "boolean", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    generators = table.Column<int>(type: "integer", nullable: false),
                    vus_per_generator = table.Column<int>(type: "integer", nullable: false),
                    ramp_seconds = table.Column<int>(type: "integer", nullable: false),
                    hold_seconds = table.Column<int>(type: "integer", nullable: false),
                    @params = table.Column<string>(name: "params", type: "text", nullable: false),
                    fleet_size_requested = table.Column<int>(type: "integer", nullable: true),
                    fleet_size_before = table.Column<int>(type: "integer", nullable: true),
                    restore_fleet = table.Column<bool>(type: "boolean", nullable: false),
                    hard_stop_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    checked_in = table.Column<int>(type: "integer", nullable: false),
                    checkins = table.Column<string>(type: "text", nullable: false, defaultValue: "{}"),
                    queued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ramp_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    stop_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    stop_reason = table.Column<string>(type: "text", nullable: true),
                    verdict = table.Column<string>(type: "text", nullable: true),
                    detail = table.Column<string>(type: "text", nullable: true),
                    peaks = table.Column<string>(type: "text", nullable: false),
                    timeline = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_load_run", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "load_run_stat",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    run_id = table.Column<long>(type: "bigint", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    index = table.Column<int>(type: "integer", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_load_run_stat", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "load_scenario",
                columns: table => new
                {
                    name = table.Column<string>(type: "text", nullable: false),
                    repository = table.Column<string>(type: "text", nullable: false),
                    commit_sha = table.Column<string>(type: "text", nullable: true),
                    script = table.Column<string>(type: "text", nullable: false),
                    script_version = table.Column<int>(type: "integer", nullable: false),
                    image = table.Column<string>(type: "text", nullable: false),
                    tag = table.Column<string>(type: "text", nullable: false),
                    params_default = table.Column<string>(type: "text", nullable: false),
                    max_vus_per_generator = table.Column<int>(type: "integer", nullable: false),
                    last_validation_run_id = table.Column<long>(type: "bigint", nullable: true),
                    updated_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_load_scenario", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "load_scenario_version",
                columns: table => new
                {
                    scenario = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    script = table.Column<string>(type: "text", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_load_scenario_version", x => new { x.scenario, x.version });
                });

            migrationBuilder.CreateIndex(
                name: "IX_load_run_scenario_id",
                table: "load_run",
                columns: new[] { "scenario", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_load_run_status",
                table: "load_run",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_load_run_stat_run_id_at",
                table: "load_run_stat",
                columns: new[] { "run_id", "at" });

            // One non-terminal load run at a time (docs/load.md section 3), enforced by
            // Postgres so two concurrent Starts cannot both insert. A constant-expression
            // partial index is Postgres-only, so other providers rely on the store's check.
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql(
                    "CREATE UNIQUE INDEX ux_load_run_active ON load_run ((true)) "
                    + "WHERE status NOT IN ('succeeded', 'stopped', 'expired', 'failed', 'lost');");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "load_run");

            migrationBuilder.DropTable(
                name: "load_run_stat");

            migrationBuilder.DropTable(
                name: "load_scenario");

            migrationBuilder.DropTable(
                name: "load_scenario_version");
        }
    }
}
