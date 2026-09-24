using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PresenterAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PresentationRevisions : Migration
    {
        /// <summary>Raised by <see cref="Down"/> while version history exists.</summary>
        public const string HistoryGuardMessage =
            "presentation_revisions holds version history; export it before rolling back (plan 010 §5)";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "presentation_revisions",
                columns: table => new
                {
                    presentation_id = table.Column<string>(type: "text", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    script = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    summary = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    base_version = table.Column<int>(type: "integer", nullable: true),
                    reverted_from = table.Column<int>(type: "integer", nullable: true),
                    changed_slides = table.Column<int[]>(type: "integer[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_presentation_revisions", x => new { x.presentation_id, x.number });
                    table.CheckConstraint("ck_presentation_revisions_source", "source IN ('import', 'live_edit', 'revert')");
                    table.ForeignKey(
                        name: "FK_presentation_revisions_presentations_presentation_id",
                        column: x => x.presentation_id,
                        principalTable: "presentations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Plan 010 §4.3: every existing presentation's current script becomes revision 1, and the version counter
            // (read nowhere before this plan) becomes "current revision number".
            migrationBuilder.Sql("""
                INSERT INTO presentation_revisions
                    (presentation_id, number, script, source, summary, base_version, reverted_from, changed_slides, created_at, created_by)
                SELECT id, 1, script, 'import', 'Version before history', NULL, NULL, '{}', updated_at, NULL
                FROM presentations;
                """);
            migrationBuilder.Sql("UPDATE presentations SET version = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Destructive, so guarded (plan 010 §4.3, §5 Rollback): refuse while any presentation has more than one
            // revision. The exception aborts the migration transaction, leaving the table and its rows intact.
            migrationBuilder.Sql($"""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM presentation_revisions GROUP BY presentation_id HAVING count(*) > 1) THEN
                        RAISE EXCEPTION '{HistoryGuardMessage}';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropTable(
                name: "presentation_revisions");
        }
    }
}
