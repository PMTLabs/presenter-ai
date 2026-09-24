using Microsoft.Extensions.Time.Testing;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Scripts;
using Xunit;

namespace PresenterAi.Application.Tests.Presenting;

public sealed class PresenterPauseCloseTests
{
    [Fact]
    public async Task Two_minutes_paused_closes_and_disposes_the_upstream_but_stays_paused()
    {
        await using var h = Create();
        await h.Start();
        await h.Suspend();
        Assert.Equal("paused", h.Presenter.Snapshot().State);
        Assert.True(h.Presenter.Snapshot().Suspended);
        Assert.Equal(1, h.Sessions[0].DisposeCount);
        Assert.Contains(h.Sessions[0].Sent, s => s.Type == "close");
        Assert.Empty(h.Closed);
        Assert.Contains(h.Statuses, s => s.Status == "suspended");
    }

    [Fact]
    public async Task Suspended_talk_sends_no_audio()
    {
        await using var h = Create();
        await h.Start();
        await h.Suspend();
        Assert.False(await h.Presenter.SendAudioAsync(new byte[32]));
        Assert.DoesNotContain(h.Sessions[0].Sent, s => s.Type == "audio");
    }

    [Fact]
    public async Task Resume_after_suspend_opens_a_new_upstream_and_re_presents_the_current_slide()
    {
        await using var h = Create();
        await h.Start();
        await h.Suspend();
        Assert.True(await h.Presenter.ResumeAsync());
        Assert.Equal(2, h.Sessions.Count);
        Assert.False(h.Presenter.Snapshot().Suspended);
        Assert.Equal("presenting", h.Presenter.Snapshot().State);
        Assert.Contains(h.Sessions[1].Sent, s => s.EventId == "slide-1-part-1");
        Assert.Equal(["suspended", "reconnecting", "live"], h.Statuses.Select(s => s.Status));
    }

    [Fact]
    public async Task Next_after_suspend_reconnects_then_presents_the_target_slide()
    {
        await using var h = Create();
        await h.Start();
        await h.Suspend();
        Assert.True(await h.Presenter.NextAsync());
        Assert.Equal(2, h.Sessions.Count);
        Assert.Equal(1, h.Presenter.Snapshot().SlideIndex);
        Assert.Contains(h.Sessions[1].Sent, s => s.EventId == "slide-2-part-1");
    }

    [Fact]
    public async Task Mute_is_reapplied_after_reconnect()
    {
        await using var h = Create();
        await h.Start();
        await h.Presenter.MuteAsync();
        await h.Suspend();
        await h.Presenter.ResumeAsync();
        Assert.Contains(h.Sessions[1].Sent, s => s.Type == "mute");
    }

    [Fact]
    public async Task Reconnect_failure_ends_with_reconnect_failed()
    {
        await using var h = Create(index => new FakeSession { FailConnect = index > 0 });
        await h.Start();
        await h.Suspend();
        Assert.False(await h.Presenter.ResumeAsync());
        await h.Settle();
        Assert.Equal(EndReasons.ReconnectFailed, Assert.Single(h.Closed).EndReason);
        Assert.Equal("startup error", h.Closed[0].Reason);
    }

    [Fact]
    public async Task Max_length_ends_a_suspended_talk_without_an_upstream()
    {
        await using var h = Create();
        await h.Start();
        var started = h.Clock.GetUtcNow();
        await h.Suspend();
        h.Clock.Advance(TimeSpan.FromMinutes(5) - (h.Clock.GetUtcNow() - started));
        await h.Settle();
        Assert.Equal(EndReasons.MaxLength, Assert.Single(h.Closed).EndReason);
        Assert.Equal("suspended", h.Closed[0].Reason);
        Assert.Equal(started.AddMinutes(5), h.Closed[0].EndedAt);
        Assert.Single(h.Sessions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Usage_totals_both_segments_and_is_unconfirmed_if_one_segment_timed_out(bool timedOut)
    {
        await using var h = Create(index => new FakeSession { CloseSeconds = index == 0 ? (timedOut ? null : 7) : 11 });
        await h.Start();
        await h.Suspend();
        await h.Presenter.ResumeAsync();
        await h.Presenter.EndAsync();
        await h.Settle();
        var closed = Assert.Single(h.Closed);
        Assert.Equal(!timedOut, closed.UsageConfirmed);
        Assert.Equal(timedOut ? null : 18, closed.Seconds);
        Assert.True(closed.EstimatedSeconds >= 120);
    }

    [Fact]
    public async Task Late_closed_event_of_the_suspended_session_is_ignored()
    {
        await using var h = Create();
        await h.Start();
        await h.Suspend();
        h.Sessions[0].Drop();
        await h.Settle();
        Assert.Empty(h.Closed);
        Assert.Equal("paused", h.Presenter.Snapshot().State);
    }

    [Fact]
    public async Task Resume_within_grace_keeps_the_same_upstream()
    {
        await using var h = Create();
        await h.Start();
        await h.Presenter.PauseAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(119));
        await h.Settle();
        await h.Presenter.ResumeAsync();
        Assert.Single(h.Sessions);
        Assert.Equal(0, h.Sessions[0].DisposeCount);
    }

    [Fact]
    public async Task Reconnect_held_across_the_cap_is_cancelled_and_ends_with_max_length()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = Create(index => new FakeSession { ConnectGate = index == 0 ? null : gate });
        await h.Start();
        var started = h.Clock.GetUtcNow();
        await h.Suspend();
        h.Clock.Advance(TimeSpan.FromMinutes(5) - (h.Clock.GetUtcNow() - started) - TimeSpan.FromSeconds(1));
        await h.Settle();
        var resume = h.Presenter.ResumeAsync();
        await h.CandidateCreated(2);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await resume);
        await h.Settle();
        Assert.Equal(EndReasons.MaxLength, Assert.Single(h.Closed).EndReason);
        Assert.Equal(started.AddMinutes(5), h.Closed[0].EndedAt);
        Assert.Equal(1, h.Sessions[1].DisposeCount);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await h.Settle();
        Assert.Equal(2, h.Sessions.Count);
    }

    [Fact]
    public async Task End_during_a_hanging_reconnect_cancels_it_and_keeps_user_reason()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = Create(index => new FakeSession { ConnectGate = index == 0 ? null : gate });
        await h.Start();
        await h.Suspend();
        var resume = h.Presenter.ResumeAsync();
        await h.CandidateCreated(2);
        await h.Presenter.EndAsync(EndReasons.User);
        Assert.False(await resume);
        await h.Settle();
        Assert.Equal(EndReasons.User, Assert.Single(h.Closed).EndReason);
        Assert.Equal(1, h.Sessions[1].DisposeCount);
    }

    [Fact]
    public async Task End_before_a_reconnect_publishes_its_connect_creates_no_upstream_and_keeps_user_reason()
    {
        await using var h = Create();
        await h.Start();
        await h.Suspend();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Presenter.UpstreamStatus += status =>
        {
            if (status.Status != "reconnecting") return;
            blocked.TrySetResult();
            release.Task.Wait(); // holds the loop after Resume is accepted, before its connect begins
        };
        var resume = h.Presenter.ResumeAsync();
        await blocked.Task;
        var end = h.Presenter.EndAsync(EndReasons.User);
        release.SetResult();
        Assert.False(await resume);
        await end;
        await h.Settle();
        Assert.Single(h.Sessions);
        Assert.Equal(EndReasons.User, Assert.Single(h.Closed).EndReason);
    }

    [Fact]
    public async Task Two_pause_close_resume_cycles_do_not_extend_the_cap()
    {
        await using var h = Create();
        await h.Start();
        var started = h.Clock.GetUtcNow();
        await h.Suspend();
        await h.Presenter.ResumeAsync();
        await h.Suspend();
        await h.Presenter.ResumeAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(5) - (h.Clock.GetUtcNow() - started));
        await h.Settle();
        Assert.Equal(EndReasons.MaxLength, Assert.Single(h.Closed).EndReason);
        Assert.Equal(started.AddMinutes(5), h.Closed[0].EndedAt);
    }

    private static Harness Create(Func<int, FakeSession>? factory = null)
    {
        var clock = new FakeTimeProvider();
        var sessions = new List<FakeSession>();
        var presenter = new Presenter((_, _) =>
        {
            var session = factory?.Invoke(sessions.Count) ?? new FakeSession();
            sessions.Add(session);
            return session;
        }, (_, id, _) => Task.FromResult(new LoadedPresentation(id,
            new PresentationMeta(id, "Title", "deck", "show", null, null, null),
            [new Slide(0, 1, "One", "First.", "notes"), new Slide(1, 2, "Two", "Second.", null)], null)),
            new PresenterSettings(MaxTalkMinutes: 5), clock);
        return new Harness(presenter, clock, sessions);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(Presenter presenter, FakeTimeProvider clock, List<FakeSession> sessions)
        {
            Presenter = presenter;
            Clock = clock;
            Sessions = sessions;
            Presenter.Closed += Closed.Add;
            Presenter.UpstreamStatus += Statuses.Add;
        }

        public Presenter Presenter { get; }
        public FakeTimeProvider Clock { get; }
        public List<FakeSession> Sessions { get; }
        public List<PresenterClosed> Closed { get; } = [];
        public List<PresenterUpstreamStatus> Statuses { get; } = [];
        public Task Start() => Presenter.StartAsync("deck", null, "owner");
        public Task Settle() => Presenter.WaitUntilIdleAsync();
        public async Task Suspend()
        {
            await Presenter.PauseAsync();
            Clock.Advance(TimeSpan.FromSeconds(120));
            await Settle();
        }

        public async Task CandidateCreated(int count)
        {
            while (Sessions.Count < count) await Task.Yield();
        }

        public ValueTask DisposeAsync() => Presenter.DisposeAsync();
    }
}
