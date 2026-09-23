namespace PresenterAi.Application.Presenting;

public sealed record PresenterSettings(
    int AdvanceSilenceMs = 3000,
    string Voice = "marin",
    int FollowUpWaitMs = Presenter.DefaultFollowUpWaitMs,
    int MaxInlineTools = PresenterAi.Application.Tools.ToolsOptions.DefaultMaxInlineTools,
    int MaxTalkMinutes = 60,
    int MaxTalkCeilingMinutes = 120,
    int PauseGraceSeconds = 120,
    int IdleTimeoutSeconds = 300);
