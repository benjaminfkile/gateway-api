using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gateway.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Node stats on the heartbeat (tech-spec §4.4, docs/load.md section 4): cpu,
            // memory and disk percent and the hub connection count, null when unread.
            migrationBuilder.AddColumn<double>(
                name: "cpu",
                table: "instance_status",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "memory",
                table: "instance_status",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "disk",
                table: "instance_status",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "hub_connections",
                table: "instance_status",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "cpu", table: "instance_status");
            migrationBuilder.DropColumn(name: "memory", table: "instance_status");
            migrationBuilder.DropColumn(name: "disk", table: "instance_status");
            migrationBuilder.DropColumn(name: "hub_connections", table: "instance_status");
        }
    }
}
