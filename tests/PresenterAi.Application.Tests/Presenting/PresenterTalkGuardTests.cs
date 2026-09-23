using Microsoft.Extensions.Time.Testing;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Scripts;
using Xunit;

namespace PresenterAi.Application.Tests.Presenting;

public sealed class PresenterTalkGuardTests
{
    [Theory]
    [InlineData("presenting")]
    [InlineData("paused")]
    [InlineData("question_hold")]
    [InlineData("stalled_slide")]
    public async Task Max_length_ends_from_presenting_paused_question_hold_stalled_slide(string state)
    {
        await using var h = Create();
        await h.Presenter.StartAsync("deck", null, "owner");
        var started = h.Clock.GetUtcNow();
        if (state == "paused") await h.Presenter.PauseAsync();
        if (state == "question_hold") { h.Sessions[0].Hear("why?"); await h.Settle(); }
        if (state == "stalled_slide") { h.Clock.Advance(TimeSpan.FromSeconds(46)); await h.Settle(); }
        h.Clock.Advance(TimeSpan.FromMinutes(5) - (h.Clock.GetUtcNow() - started));
        await h.Settle();
        Assert.Equal(EndReasons.MaxLength, Assert.Single(h.Closed).EndReason);
        Assert.Equal(started.AddMinutes(5), h.Closed[0].EndedAt);
        Assert.Single(h.Sessions);
        Assert.Equal(1, h.Sessions[0].DisposeCount);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.Settle();
        Assert.Single(h.Sessions);
    }

    [Theory]
    [InlineData(2, null, 120, 5)]
    [InlineData(90, 20, 120, 20)]
    [InlineData(150, null, 120, 120)]
    public async Task Effective_cap_is_min_of_script_override_and_ceiling(
        int script, int? requested, int ceiling, int effective)
    {
        await using var h = Create(script, new PresenterSettings(MaxTalkMinutes: 60, MaxTalkCeilingMinutes: ceiling));
        await h.Presenter.StartAsync("deck", null, "owner", requested);
        h.Clock.Advance(TimeSpan.FromMinutes(effective));
        await h.Settle();
        Assert.Equal(EndReasons.MaxLength, Assert.Single(h.Closed).EndReason);
    }

    [Fact]
    public async Task Slow_load_and_connect_count_against_the_cap()
    {
        var clock = new FakeTimeProvider();
        var loading = new TaskCompletionSource<LoadedPresentation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = 0;
        await using var presenter = new Presenter((_, _) => { created++; return new FakeSession(); },
            (_, _, _) => { enteredLoad.TrySetResult(); return loading.Task; },
            new PresenterSettings(MaxTalkMinutes: 5), clock);
        var closed = new List<PresenterClosed>();
        presenter.Closed += closed.Add;
        var startedAt = clock.GetUtcNow();
        var start = presenter.StartAsync("deck", null, "owner");
        await enteredLoad.Task;
        clock.Advance(TimeSpan.FromMinutes(5));
        loading.SetResult(new LoadedPresentation("deck",
            new PresentationMeta("deck", "Title", "deck", "show", null, null, null),
            [new Slide(0, 1, "One", "Narration.", null)], null));
        Assert.False((await start).Started);
        await presenter.WaitUntilIdleAsync();
        Assert.Equal(0, created);
        Assert.Equal(EndReasons.MaxLength, Assert.Single(closed).EndReason);
        Assert.Equal(startedAt.AddMinutes(5), closed[0].EndedAt);

        var connectClock = new FakeTimeProvider();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var candidates = new List<FakeSession>();
        await using var connectingPresenter = new Presenter((_, _) =>
        {
            var candidate = new FakeSession { ConnectGate = gate };
            candidates.Add(candidate);
            return candidate;
        }, (_, id, _) => Task.FromResult(new LoadedPresentation(id,
            new PresentationMeta(id, "Title", "deck", "show", null, null, null),
            [new Slide(0, 1, "One", "Narration.", null)], null)),
            new PresenterSettings(MaxTalkMinutes: 5), connectClock);
        var connectClosed = new List<PresenterClosed>();
        connectingPresenter.Closed += connectClosed.Add;
        var connectStart = connectingPresenter.StartAsync("deck", null, "owner");
        while (candidates.Count == 0) await Task.Yield();
        var connectStartedAt = connectClock.GetUtcNow();
        connectClock.Advance(TimeSpan.FromMinutes(5));
        Assert.False((await connectStart).Started);
        await connectingPresenter.WaitUntilIdleAsync();
        Assert.Single(candidates);
        Assert.Equal(1, candidates[0].DisposeCount);
        Assert.Equal(connectStartedAt.AddMinutes(5), Assert.Single(connectClosed).EndedAt);
    }

    [Fact]
    public async Task Above_ceiling_cap_is_clamped_with_a_warning_log()
    {
        await using var h = Create(150);
        await h.Presenter.StartAsync("deck", null, "owner");
        Assert.Contains(h.Logs, log => log.Level == "warn" && log.Message.Contains("clamped 150"));
    }

    [Fact]
    public async Task Override_above_the_cap_is_ignored()
    {
        await using var h = Create(10);
        await h.Presenter.StartAsync("deck", null, "owner", 90);
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        await h.Settle();
        Assert.Equal(EndReasons.MaxLength, Assert.Single(h.Closed).EndReason);
    }

    [Fact]
    public async Task Idle_ends_after_five_minutes_without_activity()
    {
        await using var h = Create(settings: new PresenterSettings(MaxTalkMinutes: 10));
        await h.Presenter.StartAsync("deck", null, "owner");
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        await h.Settle();
        Assert.Equal(EndReasons.Idle, Assert.Single(h.Closed).EndReason);
    }

    [Fact]
    public async Task Activity_just_before_the_idle_deadline_resets_it()
    {
        await using var h = Create(settings: new PresenterSettings(MaxTalkMinutes: 15));
        await h.Presenter.StartAsync("deck", null, "owner");
        h.Clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        await h.Settle();
        h.Sessions[0].Hear("question");
        await h.Settle();
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await h.Settle();
        Assert.Empty(h.Closed);
        h.Clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        await h.Settle();
        Assert.Equal(EndReasons.Idle, Assert.Single(h.Closed).EndReason);
    }

    [Fact]
    public async Task Mic_frames_are_not_activity()
    {
        await using var h = Create(settings: new PresenterSettings(MaxTalkMinutes: 10));
        await h.Presenter.StartAsync("deck", null, "owner");
        h.Clock.Advance(TimeSpan.FromMinutes(4));
        await h.Presenter.SendAudioAsync(new byte[32]);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.Settle();
        Assert.Equal(EndReasons.Idle, Assert.Single(h.Closed).EndReason);
    }

    [Fact]
    public async Task Idle_does_not_run_while_paused()
    {
        await using var h = Create(settings: new PresenterSettings(MaxTalkMinutes: 10));
        await h.Presenter.StartAsync("deck", null, "owner");
        await h.Presenter.PauseAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        await h.Settle();
        Assert.Empty(h.Closed);
    }

    [Fact]
    public async Task Warning_sixty_seconds_before_max_length_is_raised_and_spoken_once()
    {
        await using var h = Create();
        await h.Presenter.StartAsync("deck", null, "owner");
        h.Clock.Advance(TimeSpan.FromMinutes(4));
        await h.Settle();
        Assert.Single(h.Warnings, w => w.Kind == EndReasons.MaxLength && w.SecondsLeft == 60);
        Assert.Single(h.Sessions[0].Sent, s => s.EventId == "limit-max_length-warning");
    }

    [Fact]
    public async Task Idle_warning_is_cleared_by_activity()
    {
        await using var h = Create();
        await h.Presenter.StartAsync("deck", null, "owner");
        h.Clock.Advance(TimeSpan.FromMinutes(4));
        await h.Settle();
        h.Sessions[0].Hear("question");
        await h.Settle();
        Assert.Contains(h.Warnings, w => w.Kind == EndReasons.Idle && w.SecondsLeft is null);
    }

    [Fact]
    public async Task No_spoken_warning_while_paused()
    {
        await using var h = Create();
        await h.Presenter.StartAsync("deck", null, "owner");
        await h.Presenter.PauseAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(4));
        await h.Settle();
        Assert.Contains(h.Warnings, w => w.Kind == EndReasons.MaxLength);
        Assert.DoesNotContain(h.Sessions[0].Sent, s => s.EventId == "limit-max_length-warning");
    }

    [Fact]
    public async Task Deadline_elapsing_while_ending_is_ignored()
    {
        await using var h = Create();
        await h.Presenter.StartAsync("deck", null, "owner");
        await h.Presenter.EndAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        await h.Settle();
        Assert.Single(h.Closed);
        Assert.Equal(EndReasons.User, h.Closed[0].EndReason);
    }

    [Theory]
    [MemberData(nameof(ExternalReasons))]
    public async Task End_reasons_are_in_the_vocabulary(string reason)
    {
        await using var h = Create();
        await h.Presenter.StartAsync("deck", null, "owner");
        await h.Presenter.EndAsync(reason);
        await h.Settle();
        var closed = Assert.Single(h.Closed);
        Assert.Equal(reason, closed.EndReason);
        Assert.Equal("close_requested", closed.Reason);
        Assert.Contains(closed.EndReason, EndReasons.All);
    }

    public static IEnumerable<object[]> ExternalReasons() => EndReasons.All
        .Where(reason => reason is not EndReasons.MaxLength and not EndReasons.Idle)
        .Select(reason => new object[] { reason });

    [Fact]
    public async Task Unrequested_provider_close_maps_to_upstream_lost()
    {
        await using var h = Create();
        await h.Presenter.StartAsync("deck", null, "owner");
        h.Sessions[0].Drop();
        await h.Settle();
        Assert.Equal(EndReasons.UpstreamLost, Assert.Single(h.Closed).EndReason);
        Assert.Equal("connection_lost", h.Closed[0].Reason);
    }

    [Fact]
    public async Task Wrap_up_close_maps_to_completed()
    {
        await using var h = Create();
        await h.Presenter.StartAsync("deck", null, "owner");
        await h.Presenter.NextAsync();
        h.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.WrapUpFallbackMs + 1));
        await h.Settle();
        Assert.Equal(EndReasons.Completed, Assert.Single(h.Closed).EndReason);
        Assert.Equal("close_requested", h.Closed[0].Reason);
    }

    [Fact]
    public async Task Close_timeout_keeps_requested_end_reason_and_provider_reason()
    {
        await using var h = Create();
        await h.Presenter.StartAsync("deck", null, "owner");
        h.Sessions[0].CloseSeconds = null;
        await h.Presenter.EndAsync(EndReasons.User);
        await h.Settle();
        Assert.Equal(EndReasons.User, Assert.Single(h.Closed).EndReason);
        Assert.Equal("close_timeout", h.Closed[0].Reason);
        Assert.False(h.Closed[0].UsageConfirmed);
    }

    private static Harness Create(int? scriptMax = null, PresenterSettings? settings = null)
    {
        var clock = new FakeTimeProvider();
        var sessions = new List<FakeSession>();
        var presenter = new Presenter((request, _) =>
        {
            var session = new FakeSession { Request = request };
            sessions.Add(session);
            return session;
        }, (_, id, _) => Task.FromResult(new LoadedPresentation(id,
            new PresentationMeta(id, "Title", "deck", "show", null, null, null, 1400, scriptMax),
            [new Slide(0, 1, "One", "Narration.", "Notes")], null)),
            settings ?? new PresenterSettings(MaxTalkMinutes: 5), clock);
        return new Harness(presenter, clock, sessions).Init();
    }

    private sealed class Harness(Presenter presenter, FakeTimeProvider clock, List<FakeSession> sessions) : IAsyncDisposable
    {
        public Presenter Presenter { get; } = presenter;
        public FakeTimeProvider Clock { get; } = clock;
        public List<FakeSession> Sessions { get; } = sessions;
        public List<PresenterClosed> Closed { get; } = [];
        public List<PresenterLimitWarning> Warnings { get; } = [];
        public List<PresenterLog> Logs { get; } = [];

        public async Task Settle() => await Presenter.WaitUntilIdleAsync();
        public ValueTask DisposeAsync() => Presenter.DisposeAsync();

        public Harness Init()
        {
            Presenter.Closed += Closed.Add;
            Presenter.LimitWarning += Warnings.Add;
            Presenter.Log += Logs.Add;
            return this;
        }
    }
}
