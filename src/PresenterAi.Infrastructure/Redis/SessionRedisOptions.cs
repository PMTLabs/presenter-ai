namespace PresenterAi.Infrastructure.Redis;

public sealed class SessionRedisOptions
{
    public const int MinHeartbeatIntervalSeconds = 5;
    public const int MaxHeartbeatIntervalSeconds = 60;
    public const int DefaultHeartbeatIntervalSeconds = 15;

    public const int MaxHeartbeatTimeoutSeconds = 300;
    public const int DefaultHeartbeatTimeoutSeconds = 45;

    public int TicketTtlSeconds { get; set; } = 30;
    public int AuthFrameTimeoutSeconds { get; set; } = 5;
    public int MaxPendingAuthConnections { get; set; } = 8;
    public int HeartbeatIntervalSeconds { get; set; } = DefaultHeartbeatIntervalSeconds;
    public int HeartbeatTimeoutSeconds { get; set; } = DefaultHeartbeatTimeoutSeconds;
}
