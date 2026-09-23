using Microsoft.Extensions.Time.Testing;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Scripts;
using Xunit;

namespace PresenterAi.Application.Tests.Presenting;

public sealed class PresenterVoiceCommandTests
{
    private static async Task<(Presenter Presenter, FakeSession Session, FakeTimeProvider Clock)> Start(string mode = "responses", bool failFirst = false, List<FakeSession>? sessions = null)
    {
        var clock = new FakeTimeProvider();
        FakeSession? session = null;
        var presenter = new Presenter((_, attempt) =>
        {
            session = new FakeSession { DelegationMode = mode, FailConnect = failFirst && attempt == 0 };
            sessions?.Add(session);
            return session;
        },
            (_, id, _) => Task.FromResult(new LoadedPresentation(id,
                new PresentationMeta(id, "Talk", "deck", "show", null, null, 2000, 300),
                [new Slide(0, 1, "One", "First slide.", ""), new Slide(1, 2, "Two", "Second slide.", "")], null)),
            timeProvider: clock);
        Assert.True((await presenter.StartAsync("p", null, "owner")).Started);
        return (presenter, session!, clock);
    }

    private static async Task Advance(Presenter presenter, FakeTimeProvider clock, int ms = 701)
    {
        clock.Advance(TimeSpan.FromMilliseconds(ms));
        await presenter.WaitUntilIdleAsync();
    }

    [Theory]
    [InlineData(699, "paused")]
    [InlineData(700, "presenting")]
    [InlineData(701, "presenting")]
    public async Task Gap_boundary_splits_utterances(int gap, string expected)
    {
        var (presenter, session, clock) = await Start();
        await using (presenter)
        {
            session.Hear("sto", 100, 120);
            await presenter.WaitUntilIdleAsync();
            clock.Advance(TimeSpan.FromMilliseconds(gap));
            await presenter.WaitUntilIdleAsync();
            session.Hear("p", 121, 140);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock);
            Assert.Equal(expected, presenter.Snapshot().State);
        }
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("client")]
    public async Task Out_of_range_local_number_requests_spoken_range_without_bridge(string mode)
    {
        var (presenter, session, clock) = await Start(mode);
        await using (presenter)
        {
            session.Hear("go to slide 40", 100, 200);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock);
            Assert.Equal(0, presenter.Snapshot().SlideIndex);
            Assert.Contains(session.Sent, s => s.EventId == "invalid-slide-range" && s.Content!.Contains("slides 1 to 2"));
            Assert.DoesNotContain(session.Sent, s => s.EventId?.Contains("-resume-") == true);
            var frames = 0;
            presenter.Audio += _ => frames++;
            session.Speak(startMs: 201, endMs: 250);
            await presenter.WaitUntilIdleAsync();
            Assert.Equal(1, frames);
            await Advance(presenter, clock, 701);
            await Advance(presenter, clock, 20000);
            Assert.Equal(0, presenter.Snapshot().SlideIndex);
            Assert.DoesNotContain(session.Sent, s => s.EventId?.Contains("-resume-") == true);
        }
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("client")]
    public async Task Paused_range_reply_obeys_permit_barrier(string mode)
    {
        var (presenter, session, clock) = await Start(mode);
        await using (presenter)
        {
            await presenter.PauseAsync();
            var frames = 0;
            presenter.Audio += _ => frames++;
            session.Hear("go to slide 40", 100, 200);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock);
            session.Speak(startMs: 120, endMs: 150);
            session.Speak(startMs: 201, endMs: 250);
            await presenter.WaitUntilIdleAsync();
            Assert.Equal(1, frames);
            await Advance(presenter, clock, 20000);
            Assert.Equal("paused", presenter.Snapshot().State);
            Assert.Equal(0, presenter.Snapshot().SlideIndex);
        }
    }

    [Fact]
    public async Task Single_seven_second_delta_is_not_an_instant_command()
    {
        var (presenter, session, clock) = await Start();
        await using (presenter)
        {
            session.Hear("stop", 0, 7000);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock);
            Assert.Equal("presenting", presenter.Snapshot().State);
        }
    }

    [Fact]
    public async Task Timestamp_less_command_after_quick_restart_is_not_rejected_by_previous_speech()
    {
        var sessions = new List<FakeSession>();
        var (presenter, session, clock) = await Start(sessions: sessions);
        await using (presenter)
        {
            session.Speak();
            await presenter.WaitUntilIdleAsync();
            await presenter.EndAsync();
            await presenter.WaitUntilIdleAsync();
            Assert.True((await presenter.StartAsync("p", null, "owner")).Started);
            sessions[^1].Hear("next slide", null, null);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock);
            Assert.Equal(1, presenter.Snapshot().SlideIndex);
        }
    }

    [Fact]
    public async Task Stop_end_and_continue_are_commands_not_questions()
    {
        var (presenter, session, clock) = await Start();
        await using (presenter)
        {
            var flushes = 0;
            presenter.Flush += () => flushes++;
            session.Hear("Can you stop now", 100, 200);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock);
            Assert.Equal("paused", presenter.Snapshot().State);
            Assert.Equal(1, flushes);
            session.Hear("Just end the meeting", 300, 400);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock);
            Assert.Contains(session.Sent, item => item.EventId == "end-confirmation");
            session.Hear("keep continue", 500, 600);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock);
            Assert.Equal("presenting", presenter.Snapshot().State);
            Assert.DoesNotContain(session.Sent, item => item.EventId?.Contains("-resume-1") == true);
        }
    }

    [Fact]
    public async Task During_speech_only_pause_is_eligible_and_question_word_does_not_pause()
    {
        var (presenter, session, clock) = await Start();
        await using (presenter)
        {
            session.Speak(startMs: 100, endMs: 200);
            await presenter.WaitUntilIdleAsync();
            session.Hear("next slide", 120, 160);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock);
            Assert.Equal(0, presenter.Snapshot().SlideIndex);
            session.Hear("what happens when you stop?", 300, 400);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock);
            Assert.Equal("presenting", presenter.Snapshot().State);
            session.Hear("stop", 150, 170);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock);
            Assert.Equal("paused", presenter.Snapshot().State);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Client_mode_instruction_is_appended_once(bool startupFallback)
    {
        var (presenter, session, _) = await Start("client", startupFallback);
        await using (presenter)
        {
            Assert.Single(session.Sent, item => item.EventId == "client-mode-controls");
            Assert.Contains("cannot move the slides yourself", session.Sent.Single(item => item.EventId == "client-mode-controls").Content);
        }
    }

    [Fact]
    public async Task Restart_discards_partial_utterance_and_end_confirmation_is_not_resumable()
    {
        var sessions = new List<FakeSession>();
        var (presenter, session, clock) = await Start(sessions: sessions);
        await using (presenter)
        {
            session.Hear("sto", 100, 120);
            await presenter.WaitUntilIdleAsync();
            await presenter.EndAsync();
            await presenter.WaitUntilIdleAsync();
            Assert.True((await presenter.StartAsync("p", null, "owner")).Started);
            Assert.Equal(2, sessions.Count);
            sessions[^1].Hear("p", 150, 170);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock);
            Assert.Equal("presenting", presenter.Snapshot().State);
            Assert.Equal(0, presenter.Snapshot().SlideIndex);
        }
    }

    [Theory]
    [InlineData("resume")]
    [InlineData("next")]
    [InlineData("previous")]
    [InlineData("goto")]
    public async Task Navigation_and_resume_cancel_end_confirmation(string action)
    {
        var (presenter, session, clock) = await Start();
        await using (presenter)
        {
            await presenter.RequestEndConfirmationAsync(false);
            Assert.Equal("paused", presenter.Snapshot().State);
            var changed = action switch
            {
                "resume" => await presenter.ResumeAsync(),
                "next" => await presenter.NextAsync(),
                "previous" => await presenter.PrevAsync(),
                _ => await presenter.GotoAsync(1)
            };
            Assert.True(changed);
            Assert.Equal("presenting", presenter.Snapshot().State);
            await Advance(presenter, clock, 8001);
            Assert.DoesNotContain(session.Sent, item => item.Type == "close");
            Assert.True(await presenter.RequestEndConfirmationAsync(true));
            Assert.Equal("paused", presenter.Snapshot().State);
        }
    }

    [Theory]
    [InlineData("yes", "idle")]
    [InlineData("no", "presenting")]
    public async Task End_confirmation_voice_answer_has_priority(string answer, string state)
    {
        var (presenter, session, clock) = await Start();
        await using (presenter)
        {
            await presenter.RequestEndConfirmationAsync(false);
            session.Speak(startMs: 100, endMs: 150);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock, 501);
            session.Hear(answer, 200, 250);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock);
            await presenter.WaitUntilIdleAsync();
            Assert.Equal(state, presenter.Snapshot().State);
            if (answer == "yes")
            {
                Assert.Single(session.Sent, item => item.Type == "close");
                Assert.True((await presenter.StartAsync("p", null, "owner")).Started);
                Assert.Equal(0, presenter.Snapshot().SlideIndex);
            }
            else
            {
                Assert.DoesNotContain(session.Sent, item => item.Type == "close");
            }
        }
    }

    [Fact]
    public async Task End_button_ends_immediately_even_while_confirmation_pending()
    {
        var (presenter, session, _) = await Start();
        await using (presenter)
        {
            await presenter.RequestEndConfirmationAsync(false);
            Assert.True(await presenter.EndAsync());
            await presenter.WaitUntilIdleAsync();
            Assert.Equal("idle", presenter.Snapshot().State);
            Assert.Single(session.Sent, item => item.Type == "close");
            Assert.True((await presenter.StartAsync("p", null, "owner")).Started);
            Assert.Equal(0, presenter.Snapshot().SlideIndex);
        }
    }

    [Fact]
    public async Task Resumable_takeover_drops_confirmation_and_restarts_at_same_slide()
    {
        var (presenter, _, _) = await Start();
        await using (presenter)
        {
            await presenter.GotoAsync(1);
            await presenter.RequestEndConfirmationAsync(false);
            await presenter.EndAsync(resumable: true);
            await presenter.WaitUntilIdleAsync();
            Assert.True((await presenter.StartAsync("p", null, "owner")).Started);
            Assert.Equal(1, presenter.Snapshot().SlideIndex);
        }
    }

    [Fact]
    public async Task Repeated_end_does_not_restart_the_confirmation_deadline()
    {
        var (presenter, session, clock) = await Start();
        await using (presenter)
        {
            await presenter.RequestEndConfirmationAsync(false);
            await Advance(presenter, clock, 7000);
            session.Hear("end", 100, 120);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock, 701);
            await Advance(presenter, clock, 300);
            Assert.Single(session.Sent, item => item.EventId == "end-confirmation");
            Assert.True(await presenter.RequestEndConfirmationAsync(true));
            await presenter.WaitUntilIdleAsync();
            Assert.Equal("idle", presenter.Snapshot().State);
        }
    }

    [Fact]
    public async Task New_question_during_check_in_cancels_resume_until_answered()
    {
        var (presenter, session, clock) = await Start();
        await using (presenter)
        {
            session.Hear("question", 100, 150);
            await presenter.WaitUntilIdleAsync();
            session.Speak(startMs: 151, endMs: 200);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock, 701);
            session.Hear("another question", 300, 400);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock, 5100);
            Assert.DoesNotContain(session.Sent, item => item.EventId?.Contains("-resume-") == true);
            Assert.Equal(0, presenter.Snapshot().SlideIndex);
        }
    }

    [Fact]
    public async Task Instant_pause_then_tool_pause_only_flushes_once()
    {
        var (presenter, session, clock) = await Start();
        await using (presenter)
        {
            var flushes = 0;
            presenter.Flush += () => flushes++;
            session.Hear("stop", 100, 150);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock);
            session.RaiseDelegation("responses", "d1");
            session.RaiseToolCall("d1", "c1", "pause_presentation", "{}");
            for (var attempt = 0; attempt < 20 && !session.Sent.Any(item => item.Type == "tool_output"); attempt++)
            {
                await Task.Delay(10);
                await presenter.WaitUntilIdleAsync();
            }

            Assert.Single(session.Sent, item => item.Type == "tool_output");
            Assert.Equal(1, flushes);
            Assert.Equal("paused", presenter.Snapshot().State);
        }
    }

    [Fact]
    public async Task Yes_at_check_in_resumes_without_waiting_for_quiet()
    {
        var (presenter, session, clock) = await Start();
        await using (presenter)
        {
            session.Hear("What is this?", 100, 150);
            await presenter.WaitUntilIdleAsync();
            session.Speak(startMs: 151, endMs: 200);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock, 701);
            session.Hear("yes", 300, 320);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock);
            Assert.Contains(session.Sent, item => item.EventId?.Contains("-resume-") == true);
        }
    }

    [Fact]
    public async Task Check_in_no_keeps_hold_through_voiced_output_and_silence()
    {
        var (presenter, session, clock) = await Start();
        await using (presenter)
        {
            session.Hear("What is this?", 100, 150);
            await presenter.WaitUntilIdleAsync();
            session.Speak(startMs: 151, endMs: 200);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock, 701);
            session.Hear("no", 300, 320);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock);
            session.Speak(startMs: 330, endMs: 400);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock, 20000);
            Assert.Equal(0, presenter.Snapshot().SlideIndex);
            Assert.DoesNotContain(session.Sent, item => item.EventId?.Contains("-resume-") == true);
            session.Hear("new question", 500, 550);
            await presenter.WaitUntilIdleAsync();
            session.Speak(startMs: 551, endMs: 600);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock, 701);
            await Advance(presenter, clock, 5100);
            Assert.Contains(session.Sent, item => item.EventId?.Contains("-resume-") == true);
        }
    }

    [Fact]
    public async Task Ending_disposes_audio_permit_and_flushes_playback_before_delayed_close()
    {
        var (presenter, session, clock) = await Start();
        await using (presenter)
        {
            var frames = 0;
            var flushes = 0;
            presenter.Audio += _ => frames++;
            presenter.Flush += () => flushes++;
            session.CloseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.DeferCloseEvent = true;
            var end = presenter.EndAsync();
            for (var i = 0; i < 100 && presenter.Snapshot().State != "ending"; i++) await Task.Delay(10);
            Assert.Equal("ending", presenter.Snapshot().State);
            Assert.Equal(1, flushes);
            session.CloseGate.SetResult();
            await end;
            session.Speak();
            await presenter.WaitUntilIdleAsync();
            Assert.Equal(0, frames);
            Assert.Equal(1, flushes);
            session.Close();
            await presenter.WaitUntilIdleAsync();
        }
    }

    [Fact]
    public async Task Voice_confirmed_end_flushes_again_and_discards_ending_audio()
    {
        var (presenter, session, clock) = await Start();
        await using (presenter)
        {
            var frames = 0;
            var flushes = 0;
            presenter.Audio += _ => frames++;
            presenter.Flush += () => flushes++;
            await presenter.RequestEndConfirmationAsync(false);
            session.Speak(startMs: 100, endMs: 150);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock, 501);
            var before = frames;
            session.DeferCloseEvent = true;
            session.Hear("yes", 200, 250);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock);
            Assert.Equal("ending", presenter.Snapshot().State);
            session.Speak(startMs: 260, endMs: 300);
            await presenter.WaitUntilIdleAsync();
            Assert.Equal(before, frames);
            Assert.Equal(2, flushes); // pause and end, once each
            session.Close();
            await presenter.WaitUntilIdleAsync();
        }
    }

    [Fact]
    public async Task Unvoiced_end_question_falls_back_after_eight_seconds_and_stale_yes_cannot_end()
    {
        var (presenter, session, clock) = await Start();
        await using (presenter)
        {
            await presenter.RequestEndConfirmationAsync(false);
            await Advance(presenter, clock, 7999);
            Assert.True(await presenter.RequestEndConfirmationAsync(true));
            Assert.Equal("paused", presenter.Snapshot().State);
            await Advance(presenter, clock, 2);
            await Advance(presenter, clock, 10001);
            session.Hear("yes", 100, 120);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock);
            Assert.Equal("paused", presenter.Snapshot().State);
        }
    }

    [Fact]
    public async Task Late_voiced_end_question_starts_its_deadline_after_speech()
    {
        var (presenter, session, clock) = await Start();
        await using (presenter)
        {
            await presenter.RequestEndConfirmationAsync(false);
            await Advance(presenter, clock, 1000);
            session.Speak(startMs: 100, endMs: 200);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock, 501);
            await Advance(presenter, clock, 8800);
            Assert.True(await presenter.RequestEndConfirmationAsync(true));
            Assert.Contains(presenter.Snapshot().State, new[] { "ending", "idle" });
        }
    }

    [Fact]
    public async Task Voiced_end_question_gets_full_answer_window()
    {
        var (presenter, session, clock) = await Start();
        await using (presenter)
        {
            await presenter.RequestEndConfirmationAsync(false);
            session.Speak(startMs: 100, endMs: 200);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock, 501);
            await Advance(presenter, clock, 1500);
            Assert.True(await presenter.RequestEndConfirmationAsync(true));
            Assert.Contains(presenter.Snapshot().State, new[] { "ending", "idle" });
        }
    }

    [Fact]
    public async Task Paused_audio_gate_and_confirmation_deadline_start_after_question()
    {
        var (presenter, session, clock) = await Start();
        await using (presenter)
        {
            var frames = 0;
            presenter.Audio += _ => frames++;
            Assert.True(await presenter.PauseAsync());
            Assert.DoesNotContain(session.Sent, item => item.Type == "mute");
            Assert.True(await presenter.SendAudioAsync(new byte[960]));
            session.Speak(startMs: 10, endMs: 20);
            await presenter.WaitUntilIdleAsync();
            Assert.Equal(0, frames);
            await presenter.MuteAsync();
            Assert.False(await presenter.SendAudioAsync(new byte[960]));
            await presenter.UnmuteAsync();
            Assert.True(await presenter.SendAudioAsync(new byte[960]));
            session.Hear("question", 100, 150);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock);
            session.Speak(startMs: 100, endMs: 120);
            await presenter.WaitUntilIdleAsync();
            Assert.Equal(0, frames);
            session.Speak(startMs: 151, endMs: 200);
            await presenter.WaitUntilIdleAsync();
            Assert.Equal(1, frames);
            Assert.True(await presenter.RequestEndConfirmationAsync(false));
            Assert.True(await presenter.RequestEndConfirmationAsync(true));
            Assert.Equal("paused", presenter.Snapshot().State);
            session.Speak(startMs: 250, endMs: 300);
            await presenter.WaitUntilIdleAsync();
            await Advance(presenter, clock, 501);
            Assert.True(await presenter.RequestEndConfirmationAsync(true));
            Assert.Contains(presenter.Snapshot().State, new[] { "ending", "idle" });
        }
    }
}
