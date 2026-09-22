using PresenterAi.Application.Auth;
using StackExchange.Redis;

namespace PresenterAi.Infrastructure.Redis;

public sealed class SsoStateStore : ISsoStateStore
{
    private static readonly TimeSpan NonceTtl = TimeSpan.FromMinutes(10);
    private readonly IDatabase _database;

    public SsoStateStore(IConnectionMultiplexer connection) => _database = connection.GetDatabase();

    public async Task<bool> TryConsumeNonceAsync(string nonce, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _database.StringSetAsync(
            Key(nonce),
            "1",
            NonceTtl,
            When.NotExists).ConfigureAwait(false);
    }

    private static RedisKey Key(string nonce) => $"sso:nonce:{nonce}";
}
