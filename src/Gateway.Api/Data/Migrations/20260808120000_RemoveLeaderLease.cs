using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gateway.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLeaderLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Leader election is now heartbeat-derived (tech-spec §4.3): no lock, no
            // lease, no session state. The singleton lease table added for the
            // advisory-lock scheme is no longer used — drop it.
            migrationBuilder.DropTable(
                name: "leader_lease");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "leader_lease",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    holder_instance_id = table.Column<string>(type: "text", nullable: false),
                    acquired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    renewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leader_lease", x => x.id);
                });
        }
    }
}
