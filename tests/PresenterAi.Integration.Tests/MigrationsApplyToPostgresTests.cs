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
    public async Task Migrations_apply_to_a_blank_database()
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
    }

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
