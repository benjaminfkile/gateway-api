using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gateway.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceManifestRealtimeAuthPath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "realtime_auth_path",
                table: "service_manifest",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "realtime_auth_path",
                table: "service_manifest");
        }
    }
}
