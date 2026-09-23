namespace PresenterAi.Application.Presenting;

public sealed record PresenterSettings(
    int AdvanceSilenceMs = 3000,
    string Voice = "marin",
    int FollowUpWaitMs = Presenter.DefaultFollowUpWaitMs,
    int MaxInlineTools = PresenterAi.Application.Tools.ToolsOptions.DefaultMaxInlineTools,
    int ToolTimeoutMs = 5000);
