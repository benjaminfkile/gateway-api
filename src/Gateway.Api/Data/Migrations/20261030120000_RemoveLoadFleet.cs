using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gateway.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLoadFleet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A load run never touches the fleet (docs/load.md section 3): the requested
            // size, the size before and the restore flag go.
            var columns = new[] { "fleet_size_before", "fleet_size_requested", "restore_fleet" };
            foreach (var column in columns)
            {
                if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
                {
                    migrationBuilder.DropColumn(name: column, table: "load_run");
                }
                else
                {
                    // Other providers (the Sqlite test replay) drop in place: the table
                    // rebuild EF would emit cannot recreate the bigint identity key.
                    migrationBuilder.Sql($"ALTER TABLE load_run DROP COLUMN {column};");
                }
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "fleet_size_before",
                table: "load_run",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "fleet_size_requested",
                table: "load_run",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "restore_fleet",
                table: "load_run",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
