using Microsoft.Extensions.Time.Testing;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Scripts;
using Xunit;

namespace PresenterAi.Application.Tests.Presenting;

public sealed class PresenterTests
{
    [Fact]
    public async Task Start_presents_slide_zero_with_thinking_then_instructions()
    {
        await using var harness = Create();
        Assert.True(await harness.Presenter.StartAsync("p"));
        var session = harness.Session();
        Assert.Equal("presenting", harness.Presenter.Snapshot().State);
        Assert.Equal([0], harness.Slides);
        Assert.Equal(["thinking", "instructions"], session.Sent.Select(item => item.Type));
        Assert.Equal("slide-1-notes", session.Sent[0].EventId);
        Assert.Contains("Speaker notes for slide 1 of 3", session.Sent[0].Content);
        Assert.Equal("slide-1-part-1", session.Sent[1].EventId);
        Assert.Contains("Present slide 1 of 3 (\"One\") now", session.Sent[1].Content);
    }

    [Fact]
    public async Task Output_audio_then_silence_advances_but_no_audio_does_not()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        harness.Clock.Advance(TimeSpan.FromMilliseconds(2100));
        await harness.Flush();
        Assert.Equal([0], harness.Slides);
        harness.Session().Speak();
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(1999));
        await harness.Flush();
        Assert.Equal([0], harness.Slides);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(2));
        await harness.Flush();
        Assert.Equal([0, 1], harness.Slides);
        Assert.Equal("slide-2-part-1", harness.Session().Sent[^1].EventId);
    }

    [Fact]
    public async Task Silent_output_frames_neither_start_nor_extend_narration()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        for (var index = 0; index < 30; index++)
        {
            harness.Session().Silence();
            harness.Clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        await harness.Flush();
        Assert.Equal([0], harness.Slides);
        harness.Session().Speak();
        await harness.Flush();
        for (var index = 0; index < 15; index++)
        {
            harness.Session().Silence();
            harness.Clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        harness.Clock.Advance(TimeSpan.FromMilliseconds(600));
        await harness.Flush();
        Assert.Equal([0, 1], harness.Slides);
    }

    [Fact]
    public async Task Audience_speech_during_silence_window_delays_advance()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        harness.Session().Speak();
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(1500));
        harness.Session().Hear("question?");
        await harness.Flush();
        harness.Session().Speak(startMs: 101, endMs: 200);
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(1500));
        await harness.Flush();
        Assert.Equal([0], harness.Slides);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(600));
        await harness.Flush();
        Assert.Equal([0, 1], harness.Slides);
    }

    [Fact]
    public async Task Next_clears_timer_and_navigation_interrupts()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        harness.Session().Speak();
        await harness.Flush();
        Assert.True(await harness.Presenter.NextAsync());
        Assert.Equal([0, 1], harness.Slides);
        Assert.StartsWith("Stop whatever you are saying now. Present slide 2 of 3", harness.Session().Sent[^1].Content);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(5000));
        await harness.Flush();
        Assert.Equal([0, 1], harness.Slides);
        Assert.True(await harness.Presenter.PrevAsync());
        Assert.True(await harness.Presenter.GotoAsync(2));
        Assert.False(await harness.Presenter.GotoAsync(7));
        Assert.False(await harness.Presenter.GotoAsync(-1));
        Assert.Equal([0, 1, 0, 2], harness.Slides);
    }

    [Fact]
    public async Task Pause_mutes_blocks_advance_and_resume_unmutes_and_rearms()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        harness.Session().Speak();
        await harness.Flush();
        Assert.True(await harness.Presenter.PauseAsync());
        Assert.Equal("paused", harness.Presenter.Snapshot().State);
        Assert.Equal(["mute", "instructions"], harness.Session().Sent.TakeLast(2).Select(item => item.Type));
        Assert.Contains("Pause now", harness.Session().Sent[^1].Content);
        harness.Clock.Advance(TimeSpan.FromSeconds(30));
        await harness.Flush();
        Assert.Equal([0], harness.Slides);
        Assert.False(await harness.Presenter.SendAudioAsync(new byte[960]));
        Assert.True(await harness.Presenter.ResumeAsync());
        Assert.Equal(["unmute", "instructions"], harness.Session().Sent.TakeLast(2).Select(item => item.Type));
        harness.Session().Speak();
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(2001));
        await harness.Flush();
        Assert.Equal([0, 1], harness.Slides);
    }

    [Fact]
    public async Task User_mute_is_kept_across_pause_resume_and_gates_audio()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        Assert.True(await harness.Presenter.SendAudioAsync(new byte[960]));
        await harness.Presenter.MuteAsync();
        Assert.Equal("mute", harness.Session().Sent[^1].Type);
        Assert.False(await harness.Presenter.SendAudioAsync(new byte[960]));
        await harness.Presenter.PauseAsync();
        await harness.Presenter.ResumeAsync();
        Assert.DoesNotContain("unmute", harness.Session().Sent.TakeLast(2).Select(item => item.Type));
        await harness.Presenter.UnmuteAsync();
        Assert.Equal("unmute", harness.Session().Sent[^1].Type);
        Assert.True(await harness.Presenter.SendAudioAsync(new byte[960]));
    }

    [Fact]
    public async Task Last_slide_silence_sends_wrap_up_then_closes()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p", 2);
        harness.Session().Speak();
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(2001));
        await harness.Flush();
        Assert.Equal("wrap-up", harness.Session().Sent[^1].EventId);
        harness.Session().Speak();
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(2001));
        await harness.Flush();
        await harness.Flush();
        Assert.Equal("close", harness.Session().Sent[^1].Type);
        Assert.Equal("idle", harness.Presenter.Snapshot().State);
        Assert.Equal(7, harness.Presenter.Snapshot().UsageSeconds);
    }

    [Fact]
    public async Task Wrap_up_without_audio_ends_after_fallback()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p", 2);
        await harness.Presenter.NextAsync();
        Assert.Equal("wrap-up", harness.Session().Sent[^1].EventId);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.WrapUpFallbackMs + 1));
        await harness.Flush();
        Assert.Equal("close", harness.Session().Sent[^1].Type);
        Assert.Equal("idle", harness.Presenter.Snapshot().State);
    }

    [Fact]
    public async Task Silent_model_gets_two_nudges_then_the_run_pauses_with_a_warning()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");

        harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.NudgeMs + 1));
        await harness.Flush();
        Assert.Equal(1, harness.Session().Sent.Count(item => item.EventId == "slide-1-nudge"));

        harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.NudgeMs));
        await harness.Flush();
        Assert.Equal(1, harness.Session().Sent.Count(item => item.EventId == "slide-1-nudge-2"));

        harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.NudgeMs));
        await harness.Flush();
        Assert.Equal("paused", harness.Presenter.Snapshot().State);
        Assert.Equal("mute", harness.Session().Sent[^2].Type);
        Assert.Contains(new PresenterLog("warn", "The model stopped responding on slide 1 — Resume or End."), harness.Logs);

        var sentAfterPause = harness.Session().Sent.Count;
        harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.NudgeMs * 2));
        await harness.Flush();
        Assert.Equal(sentAfterPause, harness.Session().Sent.Count);
        Assert.Equal(1, harness.Session().Sent.Count(item => item.EventId == "slide-1-nudge"));
        Assert.Equal(1, harness.Session().Sent.Count(item => item.EventId == "slide-1-nudge-2"));
    }

    [Fact]
    public async Task Voiced_output_after_the_first_nudge_cancels_the_escalation()
    {
        await using var harness = Create(advanceSilenceMs: 60_000);
        await harness.Presenter.StartAsync("p");
        harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.NudgeMs + 1));
        await harness.Flush();

        harness.Session().Speak();
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.NudgeMs * 2));
        await harness.Flush();

        Assert.Equal("presenting", harness.Presenter.Snapshot().State);
        Assert.DoesNotContain(harness.Session().Sent, item => item.EventId == "slide-1-nudge-2");
        Assert.DoesNotContain(harness.Session().Sent, item => item.Type == "mute");
    }

    [Fact]
    public async Task Resume_after_a_stall_rearms_the_first_nudge()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        for (var nudge = 0; nudge < 3; nudge++)
        {
            harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.NudgeMs + (nudge == 0 ? 1 : 0)));
            await harness.Flush();
        }

        Assert.Equal("paused", harness.Presenter.Snapshot().State);
        Assert.True(await harness.Presenter.ResumeAsync());
        harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.NudgeMs + 1));
        await harness.Flush();

        Assert.Equal("presenting", harness.Presenter.Snapshot().State);
        Assert.Equal(2, harness.Session().Sent.Count(item => item.EventId == "slide-1-nudge"));
        Assert.Equal(1, harness.Session().Sent.Count(item => item.EventId == "slide-1-nudge-2"));
    }

    [Fact]
    public async Task Resume_after_a_stall_counts_the_rest_of_the_slide_in_a_new_diagnostics_line()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        for (var nudge = 0; nudge < 3; nudge++)
        {
            harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.NudgeMs + (nudge == 0 ? 1 : 0)));
            await harness.Flush();
        }

        Assert.Contains(new PresenterLog("info", "slide 1: output 0 frames (0 voiced), user transcript 0 chars"), harness.Logs);
        Assert.True(await harness.Presenter.ResumeAsync());
        harness.Session().Speak();
        await harness.Flush();
        Assert.True(await harness.Presenter.NextAsync());

        Assert.Contains(new PresenterLog("info", "slide 1: output 1 frames (1 voiced), user transcript 0 chars"), harness.Logs);
    }

    [Fact]
    public async Task New_slide_during_escalation_starts_with_its_first_nudge()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.NudgeMs + 1));
        await harness.Flush();

        Assert.True(await harness.Presenter.NextAsync());
        harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.NudgeMs + 1));
        await harness.Flush();

        Assert.Equal(1, harness.Session().Sent.Count(item => item.EventId == "slide-1-nudge"));
        Assert.Equal(1, harness.Session().Sent.Count(item => item.EventId == "slide-2-nudge"));
        Assert.DoesNotContain(harness.Session().Sent, item => item.EventId == "slide-2-nudge-2");
    }

    [Fact]
    public async Task Slide_diagnostics_count_output_frames_and_user_transcript_characters()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        harness.Session().Silence();
        harness.Session().Speak();
        harness.Session().Silence();
        harness.Session().Hear("what");
        await harness.Flush();

        Assert.True(await harness.Presenter.NextAsync());

        Assert.Contains(new PresenterLog("info", "slide 1: output 3 frames (1 voiced), user transcript 4 chars"), harness.Logs);
    }

    [Fact]
    public async Task Upstream_drop_keeps_slide_index_and_next_start_resumes()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        await harness.Presenter.GotoAsync(1);
        harness.Session().Drop();
        await harness.Flush();
        Assert.Equal("idle", harness.Presenter.Snapshot().State);
        Assert.Equal(1, harness.Presenter.Snapshot().SlideIndex);
        await harness.Presenter.StartAsync("p");
        Assert.Equal(1, harness.Presenter.Snapshot().SlideIndex);
        Assert.Equal(1, harness.Slides[^1]);
    }

    [Fact]
    public async Task Normal_end_starts_next_run_at_zero()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        await harness.Presenter.GotoAsync(2);
        await harness.Presenter.EndAsync();
        await harness.Flush();
        await harness.Presenter.StartAsync("p");
        Assert.Equal(0, harness.Presenter.Snapshot().SlideIndex);
    }

    [Fact]
    public async Task Parts_are_sent_one_at_a_time_after_speech_gap()
    {
        var slides = LongSlides();
        await using var harness = Create(slides: slides, chunkChars: 300);
        await harness.Presenter.StartAsync("p");
        var parts = TextChunker.Chunk(slides[0].Narration, 300).Count;
        Assert.True(parts >= 3);
        var instructions = () => harness.Session().Sent.Where(item => item.Type == "instructions").ToArray();
        Assert.Single(instructions());
        for (var part = 2; part <= parts; part++)
        {
            harness.Session().Speak();
            await harness.Flush();
            harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.PartGapFor(2000) - 1));
            await harness.Flush();
            Assert.Equal(part - 1, instructions().Length);
            harness.Clock.Advance(TimeSpan.FromMilliseconds(2));
            await harness.Flush();
            Assert.Equal(part, instructions().Length);
            Assert.Equal($"slide-1-part-{part}", instructions()[^1].EventId);
        }

        harness.Session().Speak();
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(2001));
        await harness.Flush();
        Assert.Equal([0, 1], harness.Slides);
    }

    [Fact]
    public async Task Manual_navigation_drops_pending_parts()
    {
        await using var harness = Create(slides: LongSlides(), chunkChars: 300);
        await harness.Presenter.StartAsync("p");
        Assert.Contains(harness.Session().Sent, item => item.EventId == "slide-1-part-1");
        await harness.Presenter.NextAsync();
        Assert.Equal("slide-2-part-1", harness.Session().Sent.Last(item => item.Type == "instructions").EventId);
    }

    [Fact]
    public async Task Empty_narration_advances_after_silence_window()
    {
        var slides = new[]
        {
            new Slide(0, 1, "A", string.Empty, string.Empty),
            new Slide(1, 2, "B", "b", string.Empty)
        };
        await using var harness = Create(slides: slides, advanceSilenceMs: 500);
        await harness.Presenter.StartAsync("p");
        harness.Clock.Advance(TimeSpan.FromMilliseconds(501));
        await harness.Flush();
        Assert.Equal([0, 1], harness.Slides);
    }

    [Fact]
    public async Task Start_while_busy_load_failure_and_connect_failure_return_idle()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        Assert.False(await harness.Presenter.StartAsync("p"));
        await harness.Presenter.EndAsync();
        await harness.Flush();
        Assert.False(await harness.Presenter.StartAsync("missing"));
        Assert.Equal("idle", harness.Presenter.Snapshot().State);
        Assert.Contains(harness.Errors, error => error.Code == "presentation" && error.Message.Contains("no such file"));

        await using var failing = Create(failAttempts: [0]);
        Assert.False(await failing.Presenter.StartAsync("p"));
        Assert.Equal("invalid_model", failing.Errors[0].Code);
        Assert.StartsWith("primary: ", failing.Errors[0].Message);
    }

    [Fact]
    public async Task Falls_back_to_next_upstream_on_startup_error()
    {
        await using var harness = Create(upstreams: 2, failAttempts: [0]);
        var start = await harness.Presenter.StartAsync("p", null, "test-owner");
        Assert.True(start.Started);
        Assert.Equal("fallback", start.Upstream);
        Assert.Equal("sess_test", start.UpstreamSessionId);
        Assert.Equal("test", start.Model);
        Assert.Equal(2, harness.Sessions.Count);
        Assert.Equal("fallback", harness.Sessions[1].Name);
        Assert.Empty(harness.Sessions[0].Sent);
        Assert.Equal(["thinking", "instructions"], harness.Sessions[1].Sent.Select(item => item.Type));
        Assert.Single(harness.Errors);
        Assert.Equal(1, harness.Sessions[0].DisposeCount);
        Assert.Contains(new PresenterLog("warn", "connected via fallback"), harness.Logs);
    }

    [Fact]
    public async Task Successful_primary_start_logs_which_upstream_answered()
    {
        await using var harness = Create(upstreams: 2);

        Assert.True(await harness.Presenter.StartAsync("p"));

        Assert.Contains(new PresenterLog("info", "connected via primary"), harness.Logs);
        Assert.DoesNotContain(harness.Logs, log => log.Level == "warn" && log.Message.StartsWith("connected via"));
    }

    [Fact]
    public async Task All_failed_upstream_candidates_are_disposed()
    {
        await using var harness = Create(upstreams: 2, failAttempts: [0, 1]);

        var start = await harness.Presenter.StartAsync("p", null, "test-owner");
        Assert.False(start.Started);
        Assert.Null(start.Upstream);
        Assert.Equal("idle", harness.Presenter.Snapshot().State);
        Assert.All(harness.Sessions, session => Assert.Equal(1, session.DisposeCount));
    }

    [Fact]
    public async Task Normal_end_disposes_session_after_closed()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();

        Assert.True(await harness.Presenter.EndAsync());
        await harness.Flush();

        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task Dispose_with_open_session_disposes_session()
    {
        var harness = Create();
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();

        await harness.Presenter.DisposeAsync();

        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task Throwing_close_logs_error_reaches_idle_and_faults_end()
    {
        await using var harness = Create(throwOnClose: true);
        await harness.Presenter.StartAsync("p");

        var end = harness.Presenter.EndAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => end);
        Assert.Equal("close failed", exception.Message);
        await harness.Flush();
        Assert.Equal("idle", harness.Presenter.Snapshot().State);
        Assert.Contains(harness.Logs, log => log.Level == "error" && log.Message.Contains("session close failed"));
    }

    [Fact]
    public async Task Client_request_is_a_normal_close()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        await harness.Presenter.GotoAsync(2);
        harness.Session().Close();
        await harness.Flush();
        await harness.Presenter.StartAsync("p");
        Assert.Equal(0, harness.Presenter.Snapshot().SlideIndex);
    }

    [Fact]
    public async Task Goto_queued_during_timer_wins_and_sends_once()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        harness.Session().Speak();
        await harness.Flush();
        var goTo = harness.Presenter.GotoAsync(2);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(2001));
        Assert.True(await goTo);
        await harness.Flush();
        Assert.Equal(2, harness.Presenter.Snapshot().SlideIndex);
        Assert.Equal(1, harness.Session().Sent.Count(item => item.EventId == "slide-3-part-1"));
        Assert.DoesNotContain(harness.Slides, index => index == 1);
    }

    [Fact]
    public async Task Pause_during_part_gap_holds_next_part_until_resume()
    {
        await using var harness = Create(slides: LongSlides(), chunkChars: 300);
        await harness.Presenter.StartAsync("p");
        harness.Session().Speak();
        await harness.Flush();
        await harness.Presenter.PauseAsync();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.PartGapFor(2000) + 1));
        await harness.Flush();
        Assert.Equal(1, harness.Session().Sent.Count(item => item.EventId?.StartsWith("slide-1-part-", StringComparison.Ordinal) is true));
        await harness.Presenter.ResumeAsync();
        harness.Session().Speak();
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.PartGapFor(2000) + 1));
        await harness.Flush();
        Assert.Equal(4, harness.Session().Sent.Count(item => item.Type == "instructions")); // pause, resume, and part 2
    }

    [Fact]
    public async Task End_during_part_closes_once()
    {
        await using var harness = Create(slides: LongSlides(), chunkChars: 300);
        await harness.Presenter.StartAsync("p");
        harness.Session().Speak();
        await harness.Flush();
        Assert.True(await harness.Presenter.EndAsync());
        harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.PartGapFor(2000) + 1));
        await harness.Flush();
        Assert.Equal(1, harness.Session().Sent.Count(item => item.Type == "close"));
        Assert.Equal(1, harness.Session().Sent.Count(item => item.EventId == "slide-1-part-1"));
        Assert.DoesNotContain(harness.Session().Sent, item => item.EventId == "slide-1-part-2");
    }

    [Fact]
    public async Task User_speech_inside_advance_window_holds_then_rearms()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        harness.Session().Speak();
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(1500));
        harness.Session().Hear();
        await harness.Flush();
        harness.Session().Speak(startMs: 101, endMs: 200);
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(1500));
        await harness.Flush();
        Assert.Equal(0, harness.Presenter.Snapshot().SlideIndex);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(600));
        await harness.Flush();
        Assert.Equal(1, harness.Presenter.Snapshot().SlideIndex);
    }

    [Fact]
    public async Task Question_holds_slide_until_the_answer_is_voiced_and_goes_quiet()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        harness.Session().Speak();
        await harness.Flush();
        harness.Session().Hear("question", endMs: 100);
        await harness.Flush();
        harness.Session().Speak(startMs: 101, endMs: 200);
        await harness.Flush();

        harness.Clock.Advance(TimeSpan.FromMilliseconds(2001));
        await harness.Flush();

        Assert.Equal([0, 1], harness.Slides);
        Assert.Contains(harness.Logs, log => log.Message.StartsWith("question: answered after ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Question_holds_a_pending_part()
    {
        await using var harness = Create(slides: LongSlides(), chunkChars: 300);
        await harness.Presenter.StartAsync("p");
        harness.Session().Speak();
        await harness.Flush();
        harness.Session().Hear("question");
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.PartGapFor(2000) + 1));
        await harness.Flush();
        Assert.DoesNotContain(harness.Session().Sent, item => item.EventId == "slide-1-part-2");

        harness.Session().Speak(startMs: 101, endMs: 200);
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.PartGapFor(2000) + 1));
        await harness.Flush();
        Assert.Contains(harness.Session().Sent, item => item.EventId == "slide-1-part-2");
    }

    [Fact]
    public async Task Question_during_wrap_up_prevents_close()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p", 2);
        await harness.Presenter.NextAsync();
        harness.Session().Hear("question");
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.WrapUpFallbackMs + 1));
        await harness.Flush();
        Assert.DoesNotContain(harness.Session().Sent, item => item.Type == "close");

        harness.Session().Speak(startMs: 101, endMs: 200);
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(2001));
        await harness.Flush();
        Assert.Contains(harness.Session().Sent, item => item.Type == "close");
    }

    [Fact]
    public async Task Unanswered_question_releases_after_15_seconds_without_advancing()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        harness.Session().Speak();
        await harness.Flush();
        harness.Session().Hear("question");
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.QuestionHoldMs + 1));
        await harness.Flush();

        Assert.Equal([0], harness.Slides);
        Assert.Contains(new PresenterLog("info", "question: released after 15 s without an answer"), harness.Logs);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(2001));
        await harness.Flush();
        Assert.Equal([0, 1], harness.Slides);
    }

    [Fact]
    public async Task Audio_before_the_latest_user_delta_is_not_the_answer()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        harness.Session().Speak(startMs: 0, endMs: 100);
        harness.Session().Hear("question", startMs: 101, endMs: 200);
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(3000));
        await harness.Flush();

        Assert.Equal([0], harness.Slides);
        Assert.DoesNotContain(harness.Logs, log => log.Message.StartsWith("question: answered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Question_before_any_output_keeps_the_nudges()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        harness.Session().Hear("question");
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.NudgeMs + 1));
        await harness.Flush();

        Assert.Contains(harness.Session().Sent, item => item.EventId == "slide-1-nudge");
    }

    [Fact]
    public async Task Pause_and_navigation_clear_the_hold()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        harness.Session().Hear("question");
        await harness.Flush();
        Assert.True(await harness.Presenter.PauseAsync());
        harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.QuestionHoldMs + 1));
        await harness.Flush();
        Assert.DoesNotContain(harness.Logs, log => log.Message == "question: released after 15 s without an answer");

        Assert.True(await harness.Presenter.ResumeAsync());
        harness.Session().Hear("another question");
        await harness.Flush();
        Assert.True(await harness.Presenter.NextAsync());
        harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.QuestionHoldMs + 1));
        await harness.Flush();
        Assert.Equal(1, harness.Presenter.Snapshot().SlideIndex);
    }

    [Fact]
    public async Task Blank_user_delta_does_not_open_a_hold()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        harness.Session().Speak();
        await harness.Flush();
        harness.Session().Hear("   ");
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(2001));
        await harness.Flush();

        Assert.Equal([0, 1], harness.Slides);
        Assert.DoesNotContain(harness.Logs, log => log.Message == "question: hold opened");
    }

    [Fact]
    public async Task Client_delegation_gets_a_scoped_answer_now_instruction()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        harness.Session().RaiseDelegation("client", "delegation-7");
        await harness.Flush();

        var append = Assert.Single(harness.Session().Sent, item => item.EventId == "question-delegation-7-answer-now");
        Assert.Equal("delegation-7", append.DelegationId);
        Assert.Equal(PromptBuilder.ClientDelegationAnswerNowInstruction(), append.Content);
        Assert.Contains(new PresenterLog("info", "question: delegated (client)"), harness.Logs);
    }

    [Fact]
    public async Task Responses_delegation_resets_the_answer_and_rearms_the_hold()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        harness.Session().Speak();
        await harness.Flush();
        harness.Session().Hear("question");
        harness.Session().Speak(startMs: 101, endMs: 200);
        await harness.Flush();
        harness.Session().RaiseDelegation("responses", "delegation-8");
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(3000));
        await harness.Flush();

        Assert.Equal([0], harness.Slides);
        Assert.Contains(new PresenterLog("info", "question: delegated (backend)"), harness.Logs);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.QuestionHoldMs - 3000 + 1));
        await harness.Flush();
        Assert.Equal([0], harness.Slides);
    }

    [Fact]
    public async Task Backend_filler_is_not_the_answer_until_the_backend_finishes()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        harness.Session().Speak();
        await harness.Flush();
        harness.Session().Hear("question");
        await harness.Flush();
        harness.Session().RaiseDelegation("responses", "delegation-9");
        harness.Session().RaiseDelegation("responses", "delegation-9");
        await harness.Flush();
        harness.Session().Speak();
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(2001));
        await harness.Flush();

        Assert.Equal([0], harness.Slides);
        Assert.DoesNotContain(harness.Logs, log => log.Message.StartsWith("question: answered", StringComparison.Ordinal));

        harness.Session().RaiseDelegatedResponse("delegation-other");
        harness.Session().Speak();
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(2001));
        await harness.Flush();
        Assert.Equal([0], harness.Slides);

        harness.Session().RaiseDelegatedResponse("delegation-9");
        await harness.Flush();
        Assert.Contains(new PresenterLog("info", "question: backend answer ready"), harness.Logs);
        harness.Session().Speak();
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(2001));
        await harness.Flush();

        Assert.Equal([0, 1], harness.Slides);
        Assert.Contains(harness.Logs, log => log.Message.StartsWith("question: answered after ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failed_backend_answer_is_logged_and_the_next_speech_counts()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        harness.Session().Speak();
        await harness.Flush();
        harness.Session().Hear("question");
        harness.Session().RaiseDelegation("responses", "delegation-10");
        harness.Session().RaiseDelegatedResponse("delegation-10", "response.failed");
        harness.Session().Speak();
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(2001));
        await harness.Flush();

        Assert.Contains(new PresenterLog("warn", "question: backend answer failed (response.failed)"), harness.Logs);
        Assert.Equal([0, 1], harness.Slides);
    }

    [Fact]
    public async Task Delegation_while_paused_does_not_open_a_hold()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        await harness.Presenter.PauseAsync();
        harness.Session().RaiseDelegation("responses", "delegation-11");
        await harness.Flush();

        Assert.DoesNotContain(new PresenterLog("info", "question: hold opened"), harness.Logs);
    }

    [Fact]
    public async Task Session_warning_during_connect_reaches_the_log_and_the_title_reaches_the_session()
    {
        await using var harness = Create(warnOnConnect: "delegation: backend unavailable (x); answering from the deck only");
        await harness.Presenter.StartAsync("p");
        await harness.Flush();

        Assert.Contains(new PresenterLog("warn", "delegation: backend unavailable (x); answering from the deck only"), harness.Logs);
        Assert.Equal("T", harness.Session().Request?.Title);
    }

    private static Harness Create(
        IReadOnlyList<Slide>? slides = null,
        int chunkChars = 1400,
        int advanceSilenceMs = 2000,
        int upstreams = 1,
        int[]? failAttempts = null,
        bool throwOnClose = false,
        string? warnOnConnect = null)
    {
        var clock = new FakeTimeProvider();
        var sessions = new List<FakeSession>();
        var presenter = new Presenter(
            (request, attempt) =>
            {
                if (attempt >= upstreams)
                {
                    return null;
                }

                var session = new FakeSession
                {
                    Name = attempt == 0 ? "primary" : "fallback",
                    FailConnect = failAttempts?.Contains(attempt) is true,
                    ThrowOnClose = throwOnClose,
                    WarnOnConnect = warnOnConnect,
                    Request = request
                };
                sessions.Add(session);
                return session;
            },
            (_, id, _) => id == "missing"
                ? Task.FromException<LoadedPresentation>(new InvalidOperationException("no such file"))
                : Task.FromResult(new LoadedPresentation(
                    id,
                    new PresentationMeta(id, "T", "deck", "showFn", null, null, advanceSilenceMs, chunkChars),
                    slides ?? DefaultSlides,
                    "ctx")),
            new PresenterSettings(advanceSilenceMs, "marin"),
            clock);
        return new Harness(presenter, clock, sessions);
    }

    private static readonly Slide[] DefaultSlides =
    [
        new(0, 1, "One", "First slide text.", "n1"),
        new(1, 2, "Two", "Second slide text.", string.Empty),
        new(2, 3, "Three", "Third slide text.", "n3")
    ];

    private static Slide[] LongSlides() =>
    [
        new(0, 1, "L", string.Join(' ', Enumerable.Range(0, 60).Select(index => $"Sentence {index} is here.")), string.Empty),
        new(1, 2, "M", "short", string.Empty)
    ];

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(Presenter presenter, FakeTimeProvider clock, List<FakeSession> sessions)
        {
            Presenter = presenter;
            Clock = clock;
            Sessions = sessions;
            presenter.Slide += Slides.Add;
            presenter.UpstreamError += Errors.Add;
            presenter.Log += Logs.Add;
        }

        public Presenter Presenter { get; }
        public FakeTimeProvider Clock { get; }
        public List<FakeSession> Sessions { get; }
        public List<int> Slides { get; } = [];
        public List<PresenterUpstreamError> Errors { get; } = [];
        public List<PresenterLog> Logs { get; } = [];

        public FakeSession Session() => Sessions[^1];

        public Task Flush() => Presenter.WaitUntilIdleAsync();

        public ValueTask DisposeAsync() => Presenter.DisposeAsync();
    }
}
