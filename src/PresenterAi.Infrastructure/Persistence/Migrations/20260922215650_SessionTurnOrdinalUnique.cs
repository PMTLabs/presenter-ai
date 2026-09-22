using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PresenterAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionTurnOrdinalUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_session_turns_session_id_ordinal",
                table: "session_turns");

            migrationBuilder.CreateIndex(
                name: "ix_session_turns_session_id_ordinal",
                table: "session_turns",
                columns: new[] { "session_id", "ordinal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_session_turns_session_id_ordinal",
                table: "session_turns");

            migrationBuilder.CreateIndex(
                name: "ix_session_turns_session_id_ordinal",
                table: "session_turns",
                columns: new[] { "session_id", "ordinal" });
        }
    }
}
