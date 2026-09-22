using System.Text.Json;
using System.Text.Json.Serialization;
using PresenterAi.Application.Auth;
using StackExchange.Redis;

namespace PresenterAi.Infrastructure.Redis;

public sealed class SsoCodeStore : ISsoCodeStore
{
    private static readonly TimeSpan CodeTtl = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
    private readonly IDatabase _database;

    public SsoCodeStore(IConnectionMultiplexer connection) => _database = connection.GetDatabase();

    public Task IssueAsync(string codeHash, SsoCode code, CancellationToken cancellationToken = default) =>
        IssueAsync(codeHash, code, CodeTtl, cancellationToken);

    public async Task IssueAsync(
        string codeHash,
        SsoCode code,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Serialize(code, SerializerOptions);
        await _database.StringSetAsync(Key(codeHash), payload, ttl).ConfigureAwait(false);
    }

    public async Task<SsoCode?> ClaimAsync(string codeHash, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await _database.StringGetDeleteAsync(Key(codeHash)).ConfigureAwait(false);
        return value.IsNull
            ? null
            : JsonSerializer.Deserialize<SsoCode>(value.ToString(), SerializerOptions);
    }

    private static RedisKey Key(string codeHash) => $"sso:code:{codeHash}";
}
