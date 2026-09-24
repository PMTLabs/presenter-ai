using PresenterAi.Application.Presenting;

namespace PresenterAi.Infrastructure.Live;

public sealed class PresenterOptions
{
    public const int MinFollowUpWaitMs = 2_500;
    public const int MaxFollowUpWaitMs = 60_000;

    public const int MinMaxTalkMinutes = 5;
    public const int DefaultMaxTalkMinutes = 60;

    public const int MinMaxTalkCeilingMinutes = 5;
    public const int MaxMaxTalkCeilingMinutes = 240;
    public const int DefaultMaxTalkCeilingMinutes = 120;

    public const int MinPauseGraceSeconds = 30;
    public const int MaxPauseGraceSeconds = 900;
    public const int DefaultPauseGraceSeconds = 120;

    public const int MinIdleTimeoutSeconds = 120;
    public const int MaxIdleTimeoutSeconds = 1800;
    public const int DefaultIdleTimeoutSeconds = 300;

    public int AdvanceSilenceMs { get; set; } = 3000;

    public int FollowUpWaitMs { get; set; } = Presenter.DefaultFollowUpWaitMs;

    public int MaxTalkMinutes { get; set; } = DefaultMaxTalkMinutes;

    public int MaxTalkCeilingMinutes { get; set; } = DefaultMaxTalkCeilingMinutes;

    public int PauseGraceSeconds { get; set; } = DefaultPauseGraceSeconds;

    public int IdleTimeoutSeconds { get; set; } = DefaultIdleTimeoutSeconds;

    public bool LogEvents { get; set; }
}
