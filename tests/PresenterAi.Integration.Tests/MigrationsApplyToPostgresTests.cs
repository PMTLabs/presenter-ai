using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PresenterAi.Infrastructure.Persistence;
using PresenterAi.Integration.Tests.Support;
using Xunit;

namespace PresenterAi.Integration.Tests;

[Collection(IntegrationCollection.Name)]
public sealed class MigrationsApplyToPostgresTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Migrations_apply_the_required_postgres_schema()
    {
        var options = new DbContextOptionsBuilder<PresenterAiDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        await using var context = new PresenterAiDbContext(options);

        await context.Database.MigrateAsync();
        await using var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        foreach (var table in new[]
                 { "users", "external_logins", "refresh_tokens", "presentations", "sessions", "session_turns" })
        {
            (await ScalarAsync(connection, "SELECT to_regclass(@table_name)::text;", ("table_name", table)))
                .Should().Be(table);
        }

        (await ScalarAsync(connection, "SELECT extname FROM pg_extension WHERE extname = 'vector';"))
            .Should().Be("vector");

        (await IndexDefinitionAsync(connection, "ux_users_email_lower"))
            .Should().Contain("UNIQUE INDEX ux_users_email_lower").And.Contain("lower(email)");
        (await IndexDefinitionAsync(connection, "ux_presentations_owner_id_slug"))
            .Should().Contain("UNIQUE INDEX ux_presentations_owner_id_slug").And.Contain("owner_id, slug");
        (await IndexDefinitionAsync(connection, "ix_session_turns_session_id_ordinal"))
            .Should().Contain("UNIQUE INDEX ix_session_turns_session_id_ordinal").And.Contain("session_id, ordinal");
        (await IndexDefinitionAsync(connection, "ix_sessions_user_id_started_at"))
            .Should().Contain("user_id, started_at DESC");

        foreach (var foreignKey in new[]
                 {
                     "FK_external_logins_users_user_id",
                     "FK_refresh_tokens_users_user_id",
                     "FK_presentations_users_owner_id",
                     "FK_sessions_presentations_presentation_id",
                     "FK_sessions_users_user_id",
                     "FK_session_turns_sessions_session_id"
                 })
        {
            (await ScalarAsync(connection, "SELECT confdeltype::text FROM pg_constraint WHERE conname = @name;", ("name", foreignKey)))
                .Should().Be("c", $"{foreignKey} must cascade on delete");
        }

        foreach (var (table, column) in new[]
                 {
                     ("external_logins", "user_id"),
                     ("refresh_tokens", "user_id"),
                     ("presentations", "owner_id"),
                     ("sessions", "presentation_id"),
                     ("sessions", "user_id"),
                     ("session_turns", "session_id")
                 })
        {
            (await ScalarAsync(connection, """
                SELECT is_nullable
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = @table_name AND column_name = @column_name;
                """, ("table_name", table), ("column_name", column)))
                .Should().Be("NO", $"{table}.{column} is a required relationship key");
        }

        foreach (var (column, dataType) in new[]
                 {
                     ("end_reason", "text"),
                     ("usage_confirmed", "boolean"),
                     ("estimated_seconds", "integer")
                 })
        {
            (await ScalarAsync(connection, """
                SELECT data_type
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'sessions' AND column_name = @column_name;
                """, ("column_name", column)))
                .Should().Be(dataType, $"sessions.{column} must be {dataType}");

            (await ScalarAsync(connection, """
                SELECT is_nullable
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'sessions' AND column_name = @column_name;
                """, ("column_name", column)))
                .Should().Be("YES", $"sessions.{column} must be nullable");
        }
    }

    private static async Task<string> IndexDefinitionAsync(DbConnection connection, string name) =>
        (await ScalarAsync(connection, "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND indexname = @name;", ("name", name)))!;

    private static async Task<string?> ScalarAsync(
        DbConnection connection,
        string sql,
        params (string Name, string Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return (await command.ExecuteScalarAsync())?.ToString();
    }
}
