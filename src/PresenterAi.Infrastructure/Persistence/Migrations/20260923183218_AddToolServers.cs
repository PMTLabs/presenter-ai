using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PresenterAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddToolServers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tool_servers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    slug = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    auth_kind = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    last_error_code = table.Column<string>(type: "text", nullable: true),
                    always_ask = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_connected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_servers", x => x.id);
                    table.CheckConstraint("ck_tool_servers_auth_kind", "auth_kind IN ('none', 'oauth', 'header')");
                    table.CheckConstraint("ck_tool_servers_status", "status IN ('not_connected', 'connected', 'needs_reconnect', 'error')");
                    table.ForeignKey(
                        name: "FK_tool_servers_users_owner_id",
                        column: x => x.owner_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_tool_settings",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "text", nullable: false),
                    web_search_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_tool_settings", x => x.user_id);
                    table.ForeignKey(
                        name: "FK_user_tool_settings_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tool_overrides",
                columns: table => new
                {
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tool_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    always_ask = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_overrides", x => new { x.server_id, x.tool_name });
                    table.ForeignKey(
                        name: "FK_tool_overrides_tool_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "tool_servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tool_server_credentials",
                columns: table => new
                {
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    key_id = table.Column<string>(type: "text", nullable: false),
                    access_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_server_credentials", x => x.server_id);
                    table.ForeignKey(
                        name: "FK_tool_server_credentials_tool_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "tool_servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_tool_servers_owner_id_slug",
                table: "tool_servers",
                columns: new[] { "owner_id", "slug" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tool_overrides");

            migrationBuilder.DropTable(
                name: "tool_server_credentials");

            migrationBuilder.DropTable(
                name: "user_tool_settings");

            migrationBuilder.DropTable(
                name: "tool_servers");
        }
    }
}
