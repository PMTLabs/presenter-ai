using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PresenterAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionsStartedAtDescending : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_sessions_user_id_started_at",
                table: "sessions");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_user_id_started_at",
                table: "sessions",
                columns: new[] { "user_id", "started_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_sessions_user_id_started_at",
                table: "sessions");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_user_id_started_at",
                table: "sessions",
                columns: new[] { "user_id", "started_at" });
        }
    }
}
