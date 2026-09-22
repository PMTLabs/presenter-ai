namespace PresenterAi.Infrastructure.Redis;

public sealed class SessionRedisOptions
{
    public int TicketTtlSeconds { get; set; } = 30;
}
