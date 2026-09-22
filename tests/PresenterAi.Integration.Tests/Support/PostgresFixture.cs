using Testcontainers.PostgreSql;
using Xunit;

namespace PresenterAi.Integration.Tests.Support;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = CreateContainer();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        try
        {
            await _container.StartAsync();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(DockerHelp.Message, exception);
        }
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private static PostgreSqlContainer CreateContainer()
    {
        try
        {
            return new PostgreSqlBuilder()
                .WithImage("pgvector/pgvector:pg17")
                .WithDatabase("presenter_ai_test")
                .WithUsername("presenter")
                .WithPassword("test_password")
                .Build();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(DockerHelp.Message, exception);
        }
    }
}
