namespace PresenterAi.Infrastructure.Redis;

public sealed class SessionRedisOptions
{
    public int TicketTtlSeconds { get; set; } = 30;
    public int AuthFrameTimeoutSeconds { get; set; } = 5;
    public int MaxPendingAuthConnections { get; set; } = 8;
}
