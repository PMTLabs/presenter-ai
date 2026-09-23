namespace PresenterAi.Application.Presenting;

public static class EndReasons
{
    public const string User = "user";
    public const string Completed = "completed";
    public const string MaxLength = "max_length";
    public const string Idle = "idle";
    public const string Heartbeat = "heartbeat";
    public const string WriterFailed = "writer_failed";
    public const string Backpressure = "backpressure";
    public const string Disconnect = "disconnect";
    public const string Takeover = "takeover";
    public const string Shutdown = "shutdown";
    public const string Error = "error";
    public const string UpstreamLost = "upstream_lost";
    public const string ReconnectFailed = "reconnect_failed";
    public const string CliCancelled = "cli_cancelled";
    public const string CliMaxSeconds = "cli_max_seconds";
    public const string StopAfterSlide = "stop_after_slide";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        User,
        Completed,
        MaxLength,
        Idle,
        Heartbeat,
        WriterFailed,
        Backpressure,
        Disconnect,
        Takeover,
        Shutdown,
        Error,
        UpstreamLost,
        ReconnectFailed,
        CliCancelled,
        CliMaxSeconds,
        StopAfterSlide
    };
}
