using Testcontainers.Redis;
using Xunit;

namespace PresenterAi.Integration.Tests.Support;

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = CreateContainer();

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

    private static RedisContainer CreateContainer()
    {
        try
        {
            return new RedisBuilder()
                .WithImage("redis:7-alpine")
                .Build();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(DockerHelp.Message, exception);
        }
    }
}
