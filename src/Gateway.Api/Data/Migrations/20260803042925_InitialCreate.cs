using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Gateway.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "deploy_history",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    service = table.Column<string>(type: "text", nullable: false),
                    from_digest = table.Column<string>(type: "text", nullable: true),
                    to_digest = table.Column<string>(type: "text", nullable: true),
                    actor = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    detail = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deploy_history", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "deploy_instance_status",
                columns: table => new
                {
                    deploy_id = table.Column<int>(type: "integer", nullable: false),
                    instance_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    detail = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deploy_instance_status", x => new { x.deploy_id, x.instance_id });
                });

            migrationBuilder.CreateTable(
                name: "instance_status",
                columns: table => new
                {
                    instance_id = table.Column<string>(type: "text", nullable: false),
                    private_ip = table.Column<string>(type: "text", nullable: true),
                    public_ip = table.Column<string>(type: "text", nullable: true),
                    gateway_ver = table.Column<string>(type: "text", nullable: true),
                    is_leader = table.Column<bool>(type: "boolean", nullable: false),
                    services = table.Column<string>(type: "jsonb", nullable: true),
                    heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_instance_status", x => x.instance_id);
                });

            migrationBuilder.CreateTable(
                name: "service_manifest",
                columns: table => new
                {
                    name = table.Column<string>(type: "text", nullable: false),
                    image = table.Column<string>(type: "text", nullable: false),
                    tag = table.Column<string>(type: "text", nullable: false),
                    digest = table.Column<string>(type: "text", nullable: true),
                    port = table.Column<int>(type: "integer", nullable: false),
                    desired_status = table.Column<string>(type: "text", nullable: false),
                    env_secret_ref = table.Column<string>(type: "text", nullable: true),
                    include_in_health = table.Column<bool>(type: "boolean", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_manifest", x => x.name);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deploy_history");

            migrationBuilder.DropTable(
                name: "deploy_instance_status");

            migrationBuilder.DropTable(
                name: "instance_status");

            migrationBuilder.DropTable(
                name: "service_manifest");
        }
    }
}
