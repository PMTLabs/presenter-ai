using PresenterAi.Application.Presenting;

namespace PresenterAi.Infrastructure.Live;

public sealed class PresenterOptions
{
    public const int MinFollowUpWaitMs = 2_500;
    public const int MaxFollowUpWaitMs = 60_000;

    public int AdvanceSilenceMs { get; set; } = 3000;

    public int FollowUpWaitMs { get; set; } = Presenter.DefaultFollowUpWaitMs;

    public bool LogEvents { get; set; }
}
