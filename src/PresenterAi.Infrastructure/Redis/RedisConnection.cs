using StackExchange.Redis;

namespace PresenterAi.Infrastructure.Redis;

internal static class RedisConnection
{
    public static IConnectionMultiplexer Connect(string connectionString)
    {
        try
        {
            return ConnectionMultiplexer.Connect(new ConfigurationOptions
            {
                EndPoints = { connectionString },
                AbortOnConnectFail = true
            });
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Unable to connect to the required Redis service from ConnectionStrings:Redis.",
                exception);
        }
    }
}
