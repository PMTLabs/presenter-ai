using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PresenterAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionBillingGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "end_reason",
                table: "sessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "estimated_seconds",
                table: "sessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "usage_confirmed",
                table: "sessions",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "end_reason",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "estimated_seconds",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "usage_confirmed",
                table: "sessions");
        }
    }
}
