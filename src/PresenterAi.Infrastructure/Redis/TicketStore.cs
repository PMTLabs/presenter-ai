using Microsoft.Extensions.Options;
using PresenterAi.Application.Auth;
using StackExchange.Redis;

namespace PresenterAi.Infrastructure.Redis;

public sealed class TicketStore : ITicketStore
{
    private readonly IDatabase _database;
    private readonly TimeSpan _ticketTtl;

    public TicketStore(IConnectionMultiplexer connection, IOptions<SessionRedisOptions> options)
        : this(connection, TimeSpan.FromSeconds(options.Value.TicketTtlSeconds))
    {
    }

    public TicketStore(IConnectionMultiplexer connection, TimeSpan ticketTtl)
    {
        _database = connection.GetDatabase();
        _ticketTtl = ticketTtl;
    }

    public Task IssueAsync(string ticketId, string userId, CancellationToken cancellationToken = default) =>
        IssueAsync(ticketId, userId, _ticketTtl, cancellationToken);

    public async Task IssueAsync(
        string ticketId,
        string userId,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _database.StringSetAsync(Key(ticketId), userId, ttl).ConfigureAwait(false);
    }

    public async Task<string?> ClaimAsync(string ticketId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await _database.StringGetDeleteAsync(Key(ticketId)).ConfigureAwait(false);
        return value.IsNull ? null : value.ToString();
    }

    private static RedisKey Key(string ticketId) => $"ws:ticket:{ticketId}";
}
