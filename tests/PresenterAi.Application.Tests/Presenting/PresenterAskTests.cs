using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Presenting.Asking;
using PresenterAi.Application.Presenting.VoiceCommands;
using PresenterAi.Application.Scripts;
using PresenterAi.Application.Scripts.Revisions;
using PresenterAi.Application.Tests.Presenting.Asking;
using PresenterAi.Application.Tools;
using PresenterAi.TestSupport;
using Xunit;

namespace PresenterAi.Application.Tests.Presenting;

/// <summary>
/// Plan 011 T4: the press-to-ask exchange on the presenter loop. Runs over <see cref="FakeSession"/> (bytes, refused
/// appends, controllable unmute ack, gated close), a <see cref="FakeTimeProvider"/>, the real tool catalogue and the
/// scriptable <see cref="FakeScriptRevisionService"/>. In every emitter-site test the triggering event arrives after Ask
/// start.
/// </summary>
public sealed class PresenterAskTests
{
    private const string Owner = "owner";
    private const string Pid = "deck";

    private static readonly Slide[] BaseSlides =
    [
        new(0, 1, "One", "Slide one narration.", null),
        new(1, 2, "Two", "Slide two narration.", null),
        new(2, 3, "Three", "Slide three narration.", null)
    ];

    // ---- Emitter sites (§4.1 table) -------------------------------------------------------------------------------

    [Fact]
    public async Task Assistant_audio_while_listening_is_not_forwarded()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();
        var forwarded = h.Forwarded;

        h.S.Speak(200);
        await h.Settle();

        Assert.Equal(forwarded, h.Forwarded);
    }

    [Fact]
    public async Task New_tool_call_while_listening_is_refused_busy_without_confirmation_or_permit()
    {
        await using var h = new Harness(registry: Registry(new GateTool("confirm_tool", Task.FromResult(ToolResult.Success("ok")), confirmation: true)));
        await h.StartNarrating();
        await h.AskStart();

        h.S.RaiseToolCall("d1", "c1", "confirm_tool", "{}");
        await h.Settle();

        var output = JsonNode.Parse(h.S.Sent.Single(s => s.Type == "tool_output" && s.EventId == "c1").Content!)!;
        Assert.Contains(Presenter.AskBusyMessage, output["message"]?.ToString());
        Assert.Null(output["status"]);
        Assert.Contains("ask: tool call refused (busy)", h.Logs);
        Assert.DoesNotContain(h.Logs, l => l.Contains("waiting for yes", StringComparison.Ordinal));
        // No permit: assistant audio stays inaudible.
        var forwarded = h.Forwarded;
        h.S.Speak(200);
        await h.Settle();
        Assert.Equal(forwarded, h.Forwarded);
        Assert.DoesNotContain(h.S.Sent, s => s.Type == "continue_responses");
    }

    [Fact]
    public async Task Tool_invocation_started_before_ask_completing_while_listening_submits_nothing_and_sends_no_continue()
    {
        var gate = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = new Harness(registry: Registry(new GateTool("gate", gate.Task)));
        await h.StartNarrating();
        h.S.RaiseToolCall("d1", "c1", "gate", "{}");
        await h.Settle();
        await h.AskStart();

        gate.SetResult(ToolResult.Success("late result"));
        await Eventually(() => h.Logs.Any(l => l.Contains("tool invocation for c1 dropped", StringComparison.Ordinal)));
        h.S.RaiseDelegatedResponse("d1");
        await h.Settle();

        Assert.DoesNotContain(h.S.Sent, s => s.Type == "tool_output");
        Assert.DoesNotContain(h.S.Sent, s => s.Type == "continue_responses");
    }

    [Fact]
    public async Task Delegated_response_of_a_pre_ask_delegation_neither_continues_nor_rearms()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        h.S.RaiseDelegation("responses", "d1");
        await h.Settle();
        await h.AskStart();

        h.S.RaiseDelegatedResponse("d1");
        await h.Settle();
        await h.MicSpeech();
        await h.AskDone();
        h.S.RaiseDelegatedResponse("d1");
        await h.Settle();

        Assert.DoesNotContain(h.Logs, l => l.StartsWith("question: backend answer ready", StringComparison.Ordinal));
        Assert.DoesNotContain(h.S.Sent, s => s.Type == "continue_responses");
        // The budget armed at Ask done was not re-armed: it expires 15 s after the burst.
        await h.Advance(TimeSpan.FromMilliseconds(Presenter.AnswerStartBudgetMs + 1));
        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
    }

    [Fact]
    public async Task Approved_tool_started_before_ask_completing_while_listening_appends_nothing()
    {
        var gate = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = new Harness(registry: Registry(new GateTool("confirm_tool", gate.Task, confirmation: true)));
        await h.StartNarrating();
        h.S.RaiseToolCall("d1", "c1", "confirm_tool", "{}");
        await Eventually(() => h.S.Sent.Any(s => s.EventId == "c1"));
        await h.Settle();
        await h.Advance(TimeSpan.FromSeconds(8));
        h.S.Hear("yes", 2000, 2100);
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(701));
        await h.AskStart();

        gate.SetResult(ToolResult.Success("external payload"));
        await Eventually(() => h.Logs.Any(l => l.Contains("not announced", StringComparison.Ordinal)));
        await h.Settle();

        Assert.DoesNotContain(h.S.Sent, s => s.Type is "commentary" or "thinking" &&
            s.Content?.Contains("external payload", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Continue_responses_is_never_sent_while_listening()
    {
        await using var h = new Harness(registry: Registry(new GateTool("quick", Task.FromResult(ToolResult.Success("ok")))));
        await h.StartNarrating();
        h.S.RaiseToolCall("d1", "c1", "quick", "{}");
        await Eventually(() => h.S.Sent.Any(s => s.Type == "tool_output" && s.EventId == "c1"));
        await h.AskStart();

        // The round's response.completed arrives while listening: before Ask it would send response.create.
        h.S.RaiseDelegatedResponse("d1");
        await h.Settle();

        Assert.DoesNotContain(h.S.Sent, s => s.Type == "continue_responses");
    }

    [Fact]
    public async Task Delegation_event_while_listening_opens_no_hold()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();
        var sent = h.S.Sent.Count;

        h.S.RaiseDelegation("client", "d1");
        h.S.RaiseDelegation("responses", "d2");
        await h.Settle();

        Assert.DoesNotContain(h.Logs, l => l == "question: hold opened");
        Assert.Equal(sent, h.S.Sent.Count);
        // Nothing pending: the answer wait after Ask done is the plain budget, not the ceiling.
        await h.MicSpeech();
        await h.AskDone();
        await h.Advance(TimeSpan.FromMilliseconds(Presenter.AnswerStartBudgetMs + 1));
        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
    }

    [Fact]
    public async Task Edit_enqueued_during_listening_and_during_answering_defers_notice_and_hold_until_the_exchange_ends()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.TrainerOn();
        await h.AskStart();
        var flushes = h.Flushes;

        await h.TrainOn(0);
        await h.MicSpeech();
        await h.AskDone();
        await h.Answer();
        await h.TrainOn(0);

        Assert.DoesNotContain(h.S.Sent, s => s.Content == PromptBuilder.ScriptEditPendingInstruction());
        Assert.DoesNotContain(h.S.Sent, s => s.EventId == "slide-1-hold");
        Assert.Equal(flushes, h.Flushes);

        var before = h.S.Sent.Count;
        Assert.True(await h.Presenter.ResumeAsync());
        await h.Settle();
        var after = h.S.Sent.Skip(before).ToList();
        Assert.Equal(2, after.Count(s => s.Content == PromptBuilder.ScriptEditPendingInstruction()));
        var hold = after.FindIndex(s => s.EventId == "slide-1-hold");
        Assert.True(hold > after.FindLastIndex(s => s.Content == PromptBuilder.ScriptEditPendingInstruction()));
        Assert.Equal(flushes + 1, h.Flushes);
    }

    [Fact]
    public async Task Edit_failing_during_the_exchange_speaks_the_failure_notice_once_after_it()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.TrainerOn();
        await h.AskStart();
        var id = await h.TrainOn(1);
        Assert.True(h.Service.SetOutcome(id, EditOutcome.Failed([1], ScriptEditErrors.Cancelled)));
        h.Service.RaiseChanged(Pid);
        await h.Settle();
        await h.MicSpeech();
        await h.AskDone();
        await h.Answer();
        Assert.DoesNotContain(h.S.Sent, s => s.Content == PromptBuilder.ScriptEditFailedInstruction());

        Assert.True(await h.Presenter.ResumeAsync());
        await h.Settle();

        Assert.Single(h.S.Sent, s => s.Content == PromptBuilder.ScriptEditFailedInstruction());
    }

    [Fact]
    public async Task Edit_of_the_current_slide_failing_during_the_exchange_is_spoken_by_the_replay_after_it()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.TrainerOn();
        await h.AskStart();
        var id = await h.TrainOn(0);
        Assert.True(h.Service.SetOutcome(id, EditOutcome.Failed([0], ScriptEditErrors.Timeout)));
        h.Service.RaiseChanged(Pid);
        await h.Settle();
        await h.MicSpeech();
        await h.AskDone();
        await h.Answer();

        Assert.True(await h.Presenter.ResumeAsync());
        await h.Settle();

        // T13 live run: a separate notice before the replay's "stop whatever you are saying" is never heard.
        Assert.DoesNotContain(h.S.Sent, s => s.Content == PromptBuilder.ScriptEditFailedInstruction());
        var replay = h.S.Sent.Last(s => s.EventId == "slide-1-part-1").Content!;
        Assert.StartsWith("Stop whatever you are saying now. " + PromptBuilder.ScriptEditFailedLead(), replay);
        Assert.Single(h.S.Sent, s => s.Content?.Contains(PromptBuilder.ScriptEditFailedLead(), StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Edit_of_the_current_slide_failing_during_an_exchange_ended_by_navigation_leads_the_next_slide()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.TrainerOn();
        await h.AskStart();
        var id = await h.TrainOn(0);
        Assert.True(h.Service.SetOutcome(id, EditOutcome.Failed([0], ScriptEditErrors.Timeout)));
        h.Service.RaiseChanged(Pid);
        await h.Settle();

        // Review r4: the exchange ends by navigation, which drops its replay; the notice leads the new slide, once.
        Assert.True(await h.Presenter.GotoAsync(1));
        await h.Settle();
        var next = h.S.Sent.Last(s => s.EventId == "slide-2-part-1").Content!;
        Assert.Contains(PromptBuilder.ScriptEditFailedLead(), next);
        Assert.Single(h.S.Sent, s => s.Content?.Contains(PromptBuilder.ScriptEditFailedLead(), StringComparison.Ordinal) == true);
        Assert.DoesNotContain(h.S.Sent, s => s.Content == PromptBuilder.ScriptEditFailedInstruction());
    }

    [Theory]
    [InlineData("listening")]
    [InlineData("answering")]
    [InlineData("check-in")]
    public async Task Edit_applied_during_listening_answering_or_check_in_replays_exactly_once_after_the_exchange(string phase)
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.TrainerOn();
        var id = await h.TrainOn(0);
        await h.AskStart();
        await h.MicSpeech();
        if (phase != "listening")
        {
            await h.AskDone();
            await h.Answer();
            if (phase == "check-in") await h.Advance(TimeSpan.FromMilliseconds(701));
        }

        var before = h.S.Sent.Count;
        var flushes = h.Flushes;
        await h.Apply(id, 6, (0, "Slide one, updated."));
        Assert.DoesNotContain(h.S.Sent.Skip(before), s => s.Type == "instructions");
        Assert.Equal(flushes, h.Flushes);

        if (phase == "listening")
        {
            await h.AskDone();
            await h.Answer();
        }

        Assert.DoesNotContain(h.S.Sent.Skip(before), s => s.EventId?.StartsWith("slide-1-", StringComparison.Ordinal) == true);
        Assert.True(await h.Presenter.ResumeAsync());
        await h.Settle();
        var after = h.S.Sent.Skip(before).ToList();
        Assert.Single(after, s => s.EventId == "slide-1-updated-v6");
        Assert.Contains("Slide one, updated.", Assert.Single(after, s => s.EventId == "slide-1-part-1").Content);

        // Consumed once: a later pause and resume does not replay again.
        Assert.True(await h.Presenter.PauseAsync());
        Assert.True(await h.Presenter.ResumeAsync());
        await h.Settle();
        Assert.Single(h.S.Sent.Skip(before), s => s.EventId == "slide-1-updated-v6");
        Assert.Contains(h.S.Sent, s => s.EventId == "resume-1");
    }

    [Fact]
    public async Task Question_hold_timer_does_not_release_the_exchange_and_an_answer_after_15_s_within_budget_is_answered()
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        await h.Advance(TimeSpan.FromSeconds(10));
        h.S.RaiseDelegation("client", "d1");
        await h.Settle();

        await h.Advance(TimeSpan.FromSeconds(6));
        Assert.Empty(h.Offs);
        await h.Answer();

        Assert.DoesNotContain(h.Logs, l => l.StartsWith("question: released", StringComparison.Ordinal));
        Assert.Contains(h.Logs, l => l.StartsWith("question: answered after", StringComparison.Ordinal));
        Assert.Empty(h.Offs);
    }

    [Fact]
    public async Task No_nudge_during_the_exchange()
    {
        await using var h = new Harness();
        await h.Start();
        await h.AskStart();
        await h.MicSpeech();
        await h.Listen(TimeSpan.FromSeconds(60));
        await h.AskDone();
        await h.Advance(TimeSpan.FromSeconds(14));

        Assert.DoesNotContain(h.S.Sent, s => s.EventId?.Contains("nudge", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(h.Logs, l => l.StartsWith("no output audio", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Limit_warning_during_the_answer_defers_the_instruction_and_raises_the_frame_at_once()
    {
        await using var h = new Harness(settings: Settings(maxMinutes: 5));
        await h.Start();
        await h.AskStart();
        await h.MicSpeech();
        await h.Listen(TimeSpan.FromSeconds(237));
        await h.AskDone();
        for (var i = 0; i < 8; i++)
        {
            await h.Answer();
            await h.Advance(TimeSpan.FromMilliseconds(500));
        }

        Assert.Contains(h.Warnings, w => w.Kind == EndReasons.MaxLength && w.SecondsLeft == 60);
        Assert.DoesNotContain(h.S.Sent, s => s.EventId == "limit-max_length-warning");
        Assert.Empty(h.Offs);

        Assert.True(await h.Presenter.ResumeAsync());
        await h.Settle();
        Assert.Single(h.S.Sent, s => s.EventId == "limit-max_length-warning");
    }

    [Fact]
    public async Task Unmute_command_while_listening_is_refused_and_upstream_stays_muted()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();

        Assert.False(await h.Presenter.UnmuteAsync());
        await h.Settle();

        Assert.DoesNotContain(h.S.Sent, s => s.Type == "unmute");
        Assert.False(h.Presenter.Snapshot().Muted);
        Assert.Contains("ask: unmute refused while listening", h.Logs);
        Assert.Equal("listening", h.AskStates[^1].State);
    }

    [Fact]
    public async Task Late_burst_transcript_after_the_first_answer_audio_runs_no_command_and_keeps_answering()
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        await h.Answer();

        h.S.Hear("next slide", 0, 100);
        await h.Settle();
        await h.Answer();
        await h.Advance(TimeSpan.FromMilliseconds(701));

        Assert.Equal(0, h.Presenter.Snapshot().SlideIndex);
        Assert.DoesNotContain(h.Logs, l => l.StartsWith("voice:", StringComparison.Ordinal));
        Assert.Contains("ask: check-in", h.Logs);
        Assert.Empty(h.Offs);
    }

    [Theory]
    [InlineData("yes", "continued")]
    [InlineData("no", "waiting")]
    [InlineData("no thank you", "waiting")]
    [InlineData("yes go ahead", "continued")]
    public async Task Check_in_hears_a_real_yes_and_a_real_no(string reply, string reason)
    {
        await using var h = new Harness();
        await h.ToCheckIn();

        await h.Reply(reply);

        Assert.Equal(reason, Assert.Single(h.Offs).Reason);
        if (reason == "continued")
        {
            Assert.Single(h.S.Sent, IsResumeAfterQuestion);
        }
        else
        {
            Assert.DoesNotContain(h.S.Sent, IsResumeAfterQuestion);
            await h.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(0, h.Presenter.Snapshot().SlideIndex);
            Assert.DoesNotContain(h.S.Sent, IsResumeAfterQuestion);
        }
    }

    [Fact]
    public async Task Check_in_treats_next_slide_as_a_follow_up_not_navigation()
    {
        await using var h = new Harness();
        await h.ToCheckIn();

        await h.Reply("next slide");

        Assert.Equal(0, h.Presenter.Snapshot().SlideIndex);
        Assert.Contains("ask: unclear check-in reply; follow-up", h.Logs);
        Assert.Empty(h.Offs);
    }

    [Fact]
    public async Task Tool_originated_next_while_listening_is_refused_and_the_ask_continues()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new NextAfterGateTool(gate.Task);
        await using var h = new Harness(registry: Registry(tool));
        tool.Presenter = h.Presenter;
        await h.StartNarrating();
        h.S.RaiseToolCall("d1", "c1", "gate_next", "{}");
        await h.Settle();
        await h.AskStart();

        gate.SetResult();
        await Eventually(() => h.Logs.Contains("ask: tool command refused"));
        await h.Settle();

        Assert.Equal(0, h.Presenter.Snapshot().SlideIndex);
        Assert.Empty(h.Offs);
        Assert.True(await h.Presenter.AskExtendAsync());
    }

    [Fact]
    public async Task Wrap_up_fallback_does_not_end_the_talk_during_the_exchange()
    {
        await using var h = new Harness(slides: [BaseSlides[0]]);
        await h.StartNarrating();
        await h.Advance(TimeSpan.FromMilliseconds(3001));
        Assert.Contains("last slide finished; sending wrap-up", h.Logs);
        await h.AskStart();
        await h.MicSpeech();
        await h.Listen(TimeSpan.FromSeconds(20));
        await h.AskDone();
        await h.Advance(TimeSpan.FromSeconds(14));

        Assert.Empty(h.Closed);
        Assert.Equal("presenting", h.Presenter.Snapshot().State);
    }

    [Fact]
    public async Task Trainer_confirmation_timing_out_during_answering_defers_the_declined_notice_once()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.TrainerOn();
        await h.ToAwaitingAnswer(start: false);
        await h.Answer();
        h.S.RaiseToolCall("d1", "c1", "revise_script", "{\"feedback\":\"Shorter.\"}");
        await Eventually(() => h.S.Sent.Any(s => s.EventId == "c1"));
        await h.Settle();

        await h.Advance(TimeSpan.FromSeconds(8));
        await h.Advance(TimeSpan.FromSeconds(10));
        Assert.Contains(h.Logs, l => l.EndsWith("revise_script not confirmed", StringComparison.Ordinal));
        Assert.DoesNotContain(h.S.Sent, s => s.Content == PromptBuilder.ScriptEditDeclinedInstruction());
        Assert.Empty(h.Service.Enqueued);

        await h.Advance(TimeSpan.FromMilliseconds(701));
        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
        Assert.Single(h.S.Sent, s => s.Content == PromptBuilder.ScriptEditDeclinedInstruction());
    }

    [Fact]
    public async Task Upstream_backend_error_finishing_a_delegation_during_awaiting_answer_uses_the_answer_wait_not_the_hold()
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        h.S.RaiseDelegation("responses", "d1");
        await h.Settle();
        // ToAwaitingAnswer already advanced ResidualStartMs past the send; the error still comes 10 s after it.
        await h.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromMilliseconds(Presenter.ResidualStartMs));
        h.S.RaiseUpstreamError("backend_error", "backend failed");
        await h.Settle();

        await h.Advance(TimeSpan.FromSeconds(14));
        Assert.Empty(h.Offs);
        await h.Advance(TimeSpan.FromMilliseconds(1001));
        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
        Assert.Contains(h.Logs, l => l.StartsWith("ask: no answer within 25 s", StringComparison.Ordinal));
    }

    // ---- Answer wait (A4) -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Tool_call_outlasting_the_budget_keeps_waiting_until_the_ceiling_then_resumes_once()
    {
        var gate = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = new Harness(registry: Registry(new GateTool("slow", gate.Task)));
        await h.ToAwaitingAnswer();
        // ToAwaitingAnswer already advanced ResidualStartMs past the send; the tool call still comes 1 s after it.
        await h.Advance(TimeSpan.FromSeconds(1) - TimeSpan.FromMilliseconds(Presenter.ResidualStartMs));
        h.S.RaiseToolCall("d1", "c1", "slow", "{}");
        await h.Settle();

        await h.Advance(TimeSpan.FromSeconds(20));
        Assert.Contains("ask: answer work outstanding; waiting until the ceiling", h.Logs);
        await h.Advance(TimeSpan.FromSeconds(53));
        Assert.Empty(h.Offs);
        await h.Advance(TimeSpan.FromSeconds(1.1));
        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
        await h.Advance(TimeSpan.FromSeconds(60));
        Assert.Single(h.Offs);
        Assert.Single(h.S.Sent, IsResumeAfterQuestion);
        gate.SetResult(ToolResult.Success("late"));
    }

    [Theory]
    [InlineData("tool call")]
    [InlineData("delegated response")]
    [InlineData("backend finish")]
    [InlineData("hold open")]
    public async Task Every_question_hold_re_arm_during_an_exchange_goes_through_the_answer_wait(string trigger)
    {
        var gate = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = new Harness(registry: Registry(new GateTool("slow", gate.Task)));
        await h.ToAwaitingAnswer();
        await h.Advance(TimeSpan.FromMilliseconds(500));
        if (trigger == "delegated response") h.S.RaiseToolCall("d1", "c0", "slow", "{}");
        if (trigger == "backend finish") h.S.RaiseDelegation("responses", "d1");
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(9500));

        switch (trigger)
        {
            case "tool call": h.S.RaiseToolCall("d1", "c1", "slow", "{}"); break;
            case "delegated response": h.S.RaiseDelegatedResponse("d1"); break;
            case "backend finish": h.S.RaiseDelegatedResponse("d1"); break;
            case "hold open": h.S.RaiseDelegation("client", "d2"); break;
        }

        await h.Settle();
        await h.Advance(TimeSpan.FromSeconds(14));
        Assert.Empty(h.Offs);
        Assert.DoesNotContain("ask: answer work outstanding; waiting until the ceiling", h.Logs);
        await h.Advance(TimeSpan.FromMilliseconds(1001));
        if (trigger is "backend finish" or "hold open") Assert.Single(h.Offs);
        else Assert.Contains("ask: answer work outstanding; waiting until the ceiling", h.Logs);
        gate.SetResult(ToolResult.Success("late"));
    }

    // ---- Check-in turn-taking (P-13) ------------------------------------------------------------------------------

    [Fact]
    public async Task Delayed_question_delta_after_check_in_begins_is_ignored()
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        await h.Answer();
        h.S.Hear("and how much did it cost", 0, 100);
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(701));
        Assert.Contains("ask: check-in", h.Logs);

        await h.Advance(TimeSpan.FromMilliseconds(500));
        h.S.Hear("yes", 0, 100);
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(701));

        Assert.Empty(h.Offs);
        Assert.DoesNotContain(h.S.Sent, IsResumeAfterQuestion);
    }

    [Fact]
    public async Task Real_yes_after_one_and_a_half_seconds_of_transcript_quiet_counts()
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        await h.Answer();
        h.S.Hear("and how much did it cost", 0, 100);
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(701));
        await h.Advance(TimeSpan.FromMilliseconds(800));

        await h.Reply("yes");

        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
        Assert.Contains("question: confirmed; resuming", h.Logs);
        Assert.Single(h.S.Sent, IsResumeAfterQuestion);
    }

    [Fact]
    public async Task Real_no_after_quiet_gives_one_stay_even_after_an_ignored_late_yes()
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        await h.Answer();
        h.S.Hear("the rest of the question", 0, 100);
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(701));
        await h.Advance(TimeSpan.FromMilliseconds(300));
        h.S.Hear("yes", 0, 100);
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(1600));

        await h.Reply("no");

        Assert.Equal("waiting", Assert.Single(h.Offs).Reason);
        await h.Advance(TimeSpan.FromSeconds(10));
        Assert.Single(h.Offs);
        Assert.DoesNotContain(h.S.Sent, IsResumeAfterQuestion);
    }

    [Fact]
    public async Task Barge_in_yes_while_the_check_in_is_still_spoken_counts()
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        h.S.Speak(3000, 0, 3000);
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(701));

        // The reply overlaps the model's voiced audio on the upstream clock, and the model is still speaking.
        h.S.Hear("yes", 2500, 2800);
        await h.Settle();
        h.S.Speak(300, 3000, 3300);
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(701));

        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
        Assert.DoesNotContain(h.Logs, l => l.Contains("ignored (model speaking)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unclear_reply_gets_one_more_follow_up_then_the_timeout_decides()
    {
        await using var h = new Harness();
        await h.ToCheckIn();

        await h.Reply("what about Hanoi");
        Assert.Contains("ask: unclear check-in reply; follow-up", h.Logs);
        await h.Answer();
        await h.Advance(TimeSpan.FromMilliseconds(701));
        await h.Advance(TimeSpan.FromMilliseconds(1600));
        await h.Reply("hmm maybe");
        Assert.Contains("ask: unclear check-in reply; left to the timeout", h.Logs);
        Assert.Empty(h.Offs);

        await h.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
        Assert.Single(h.S.Sent, IsResumeAfterQuestion);
    }

    [Theory]
    [InlineData("awaiting")]
    [InlineData("answering")]
    [InlineData("check-in")]
    public async Task Continue_ends_the_exchange_in_every_answer_phase(string phase)
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        if (phase != "awaiting") await h.Answer();
        if (phase == "check-in") await h.Advance(TimeSpan.FromMilliseconds(701));

        Assert.True(await h.Presenter.ResumeAsync());
        await h.Settle();

        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
        Assert.Contains("ask: continue; resuming", h.Logs);
        Assert.Single(h.S.Sent, IsResumeAfterQuestion);
        Assert.Equal("presenting", h.Presenter.Snapshot().State);
    }

    [Fact]
    public async Task Delayed_question_delta_after_the_follow_up_timeout_changes_nothing()
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        await h.Answer();
        h.S.Hear("and how much", 0, 100);
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(701));
        // Late question fragments keep arriving, each within 1.5 s of the previous one, until the check-in times out.
        for (var i = 0; i < 6; i++)
        {
            h.S.Hear($"fragment {i}", 0, 100);
            await h.Settle();
            await h.Advance(TimeSpan.FromMilliseconds(900));
        }

        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
        var sent = h.S.Sent.Count;
        h.S.Hear("next slide", 0, 100);
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(701));

        Assert.Equal(sent, h.S.Sent.Count);
        Assert.Equal(0, h.Presenter.Snapshot().SlideIndex);
        Assert.DoesNotContain("question: hold opened", h.Logs);
        Assert.DoesNotContain(h.Logs, l => l.StartsWith("voice:", StringComparison.Ordinal));
    }

    // ---- Input keeps flowing (P-16) -------------------------------------------------------------------------------

    [Fact]
    public async Task User_mute_during_the_answer_defers_the_upstream_mute_to_the_exchange_end()
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        await h.Answer();
        var unmuteAt = h.S.Sent.FindLastIndex(s => s.Type == "unmute");

        Assert.True(await h.Presenter.MuteAsync());
        await h.Settle();
        Assert.True(h.Presenter.Snapshot().Muted);
        var audio = h.S.SentAudio.Count;
        Assert.False(await h.Presenter.SendAudioAsync(AudioLevelTests.VoicedFrame(960)));
        Assert.Equal(audio, h.S.SentAudio.Count);
        Assert.DoesNotContain(h.S.Sent.Skip(unmuteAt), s => s.Type == "mute");
        await h.Answer();
        Assert.Empty(h.Offs);

        Assert.True(await h.Presenter.ResumeAsync());
        await h.Settle();
        Assert.Single(h.S.Sent.Skip(unmuteAt), s => s.Type == "mute");
    }

    [Fact]
    public async Task No_upstream_mute_is_sent_between_ask_done_and_the_exchange_end()
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        var unmuteAt = h.S.Sent.FindLastIndex(s => s.Type == "unmute");
        await h.Answer();
        await h.Presenter.MuteAsync();
        await h.Presenter.UnmuteAsync();
        await h.Presenter.MuteAsync();
        await h.Settle();
        await h.Answer();
        await h.Advance(TimeSpan.FromMilliseconds(701));
        var beforeEnd = h.S.Sent.Count;
        Assert.DoesNotContain(h.S.Sent.Skip(unmuteAt), s => s.Type == "mute");

        await h.Reply("yes");

        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
        Assert.Single(h.S.Sent.Skip(beforeEnd), s => s.Type == "mute");
    }

    // ---- Reset serialization (D8) ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("end")]
    [InlineData("max")]
    [InlineData("takeover")]
    public async Task Reset_close_finishing_after_end_max_length_or_take_over_records_usage_once_and_publishes_nothing(string how)
    {
        await using var h = new Harness(settings: Settings(maxMinutes: 5));
        await h.StartNarrating();
        var first = h.S;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.CloseGate = gate;
        first.RefuseAudioFromChunk = 3;
        await h.AskStart();
        await h.MicSpeech();
        await h.AskDone();
        Assert.Equal("send_failed", Assert.Single(h.Offs).Reason);
        await Eventually(() => first.Sent.Any(s => s.Type == "close"));

        switch (how)
        {
            case "end": Assert.True(await h.Presenter.EndAsync()); break;
            case "takeover": Assert.True(await h.Presenter.EndAsync(EndReasons.Takeover)); break;
            default: await h.Advance(TimeSpan.FromMinutes(5)); break;
        }

        await h.Settle();
        var closed = Assert.Single(h.Closed);
        Assert.False(closed.UsageConfirmed);
        var marks = h.Marks.Count;
        var usage = h.Usages.Count;

        gate.SetResult();
        await Eventually(() => first.DisposeCount == 1);
        await h.Settle();

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(usage, h.Usages.Count);
        Assert.Equal(marks, h.Marks.Count);
        Assert.Single(h.Closed);
    }

    [Fact]
    public async Task Late_old_session_closed_after_reset_is_ignored_and_the_session_is_disposed_once()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        var first = h.S;
        first.RefuseAudioFromChunk = 2;
        await h.AskStart();
        await h.MicSpeech();
        await h.AskDone();

        await Eventually(() => first.DisposeCount == 1);
        await h.Settle();
        // FakeSession.CloseAsync raised its Closed event: the old session's SessionClosed is not the talk's.
        Assert.Empty(h.Closed);
        Assert.Equal(1, first.DisposeCount);
        var snapshot = h.Presenter.Snapshot();
        Assert.Equal("paused", snapshot.State);
        Assert.True(snapshot.Suspended);
    }

    [Fact]
    public async Task Reset_completion_while_paused_records_confirmed_usage_once_and_stays_suspended()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        h.S.RefuseAudioFromChunk = 0;
        h.S.CloseSeconds = 7;
        await h.AskStart();
        await h.MicSpeech();
        await h.AskDone();

        await Eventually(() => h.Usages.Count == 1);
        await h.Settle();
        Assert.Equal(7, Assert.Single(h.Usages).Seconds);
        Assert.True(h.Presenter.Snapshot().Suspended);
        Assert.Equal("paused", h.Presenter.Snapshot().State);

        Assert.True(await h.Presenter.EndAsync());
        await h.Settle();
        var closed = Assert.Single(h.Closed);
        Assert.True(closed.UsageConfirmed);
        Assert.Equal(7, closed.Seconds);
    }

    [Fact]
    public async Task No_paused_or_suspended_snapshot_after_closed()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        var first = h.S;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.CloseGate = gate;
        first.RefuseAudioFromChunk = 1;
        await h.AskStart();
        await h.MicSpeech();
        await h.AskDone();
        Assert.True(await h.Presenter.EndAsync());
        await h.Settle();
        var closedAt = h.Marks.FindIndex(m => m.Kind == "closed");

        gate.SetResult();
        await Eventually(() => first.DisposeCount == 1);
        await h.Settle();

        Assert.DoesNotContain(h.Marks.Skip(closedAt + 1), m => m.Kind is "state:paused" or "state:presenting" ||
            m.Kind.StartsWith("upstream:", StringComparison.Ordinal) || m.Kind == "usage");
        Assert.Equal("idle", h.Presenter.Snapshot().State);
    }

    [Fact]
    public async Task Newer_reset_makes_an_older_completion_stale()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        var first = h.S;
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.CloseGate = firstGate;
        first.RefuseAudioFromChunk = 1;
        await h.AskStart();
        await h.MicSpeech();
        await h.AskDone();
        await h.Advance(TimeSpan.FromSeconds(10));

        var secondGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Configure = (session, _) =>
        {
            session.RefuseAudioFromChunk = 1;
            session.CloseGate = secondGate;
        };
        await h.AskStart();
        var second = h.S;
        Assert.NotSame(first, second);
        await h.MicSpeech();
        await h.AskDone();
        await Eventually(() => second.Sent.Any(s => s.Type == "close"));
        await h.Settle();
        // The newer reset estimated the older segment once (usage unconfirmed from here on).
        var usages = h.Usages.Count;

        firstGate.SetResult();
        await Eventually(() => first.DisposeCount == 1);
        await h.Settle();
        Assert.Equal(usages, h.Usages.Count);

        secondGate.SetResult();
        await Eventually(() => second.DisposeCount == 1);
        await h.Settle();
        Assert.Equal(usages + 1, h.Usages.Count);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);

        Assert.True(await h.Presenter.EndAsync());
        await h.Settle();
        var closed = Assert.Single(h.Closed);
        Assert.False(closed.UsageConfirmed);
        Assert.True(closed.EstimatedSeconds >= 10);
    }

    // ---- Outcomes -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Check_in_no_keeps_the_replay_for_the_next_resume_once()
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        await h.Revert(6, (0, "Slide one, reverted."));
        await h.Answer();
        await h.Advance(TimeSpan.FromMilliseconds(701));
        await h.Reply("no");
        Assert.Equal("waiting", Assert.Single(h.Offs).Reason);
        Assert.DoesNotContain(h.S.Sent, s => s.EventId == "slide-1-updated-v6");

        Assert.True(await h.Presenter.ResumeAsync());
        await h.Settle();
        Assert.Single(h.S.Sent, s => s.EventId == "slide-1-updated-v6");
        Assert.True(await h.Presenter.PauseAsync());
        Assert.True(await h.Presenter.ResumeAsync());
        await h.Settle();
        Assert.Single(h.S.Sent, s => s.EventId == "slide-1-updated-v6");
    }

    [Fact]
    public async Task Navigation_during_the_answer_drops_notices_and_replay()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.TrainerOn();
        await h.ToAwaitingAnswer(start: false);
        await h.Answer();
        await h.TrainOn(2);
        await h.Revert(6, (0, "Slide one, reverted."));

        Assert.True(await h.Presenter.NextAsync());
        await h.Settle();

        Assert.Equal("navigated", Assert.Single(h.Offs).Reason);
        Assert.Contains("ask: dropped 1 deferred notices", h.Logs);
        Assert.DoesNotContain(h.S.Sent, s => s.Content == PromptBuilder.ScriptEditPendingInstruction());
        Assert.DoesNotContain(h.S.Sent, s => s.EventId == "slide-1-updated-v6");
        Assert.Contains(h.S.Sent, s => s.EventId == "slide-2-part-1");
    }

    [Fact]
    public async Task End_during_the_answer_drops_deferred_actions()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.TrainerOn();
        await h.ToAwaitingAnswer(start: false);
        await h.Answer();
        await h.TrainOn(1);
        await h.Revert(6, (0, "Slide one, reverted."));
        var before = h.S.Sent.Count;

        Assert.True(await h.Presenter.EndAsync());
        await h.Settle();

        Assert.Equal("ended", Assert.Single(h.Offs).Reason);
        Assert.Equal(["close"], h.S.Sent.Skip(before).Select(s => s.Type));
    }

    [Fact]
    public async Task Answer_budget_expiry_resumes_once_with_the_nudge_when_no_output_was_heard()
    {
        await using var h = new Harness();
        await h.Start();
        await h.AskStart();
        await h.MicSpeech();
        await h.AskDone();

        await h.Advance(TimeSpan.FromMilliseconds(Presenter.AnswerStartBudgetMs + 1));
        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
        Assert.Contains("ask: no answer within 15 s", h.Logs);
        Assert.Single(h.S.Sent, s => s.EventId == "resume-1");
        Assert.DoesNotContain(h.S.Sent, IsResumeAfterQuestion);

        await h.Advance(TimeSpan.FromMilliseconds(Presenter.NudgeMs + 1));
        Assert.Single(h.S.Sent, s => s.EventId == "slide-1-nudge");
        Assert.Single(h.Offs);
    }

    // ---- AC1 ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ask_during_narration_mutes_pauses_and_flushes_and_forwards_no_mic_audio()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        var flushes = h.Flushes;
        var before = h.S.Sent.Count;

        await h.AskStart();

        var sent = h.S.Sent.Skip(before).ToList();
        Assert.Equal("mute", sent[0].Type);
        Assert.Equal("pause-1", sent[1].EventId);
        // T8 regression fix: the Ask's own pause announces the question; the generic one made the model passive.
        Assert.Equal(PromptBuilder.AskPauseInstruction(), sent[1].Content);
        Assert.DoesNotContain(h.S.Sent, s => s.Content == PromptBuilder.PauseInstruction());
        Assert.Equal(flushes + 1, h.Flushes);
        Assert.Equal("paused", h.Presenter.Snapshot().State);
        Assert.True(h.Presenter.Snapshot().Paused);
        var listening = Assert.Single(h.AskStates);
        Assert.Equal(new PresenterAskState("listening", 0, Presenter.AskQuietTimeoutMs, AskRecorder.MaxRetainedMs, false, true, null), listening);
        Assert.Contains("ask: listening (from presenting)", h.Logs);

        await h.MicSpeech(2000);
        Assert.DoesNotContain(h.S.Sent, s => s.Type == "audio");
        Assert.Equal(100, h.Transcriber.Current!.AppendCount);
    }

    [Fact]
    public async Task Ten_second_and_ninety_second_silences_while_listening_send_no_part_nudge_resume_or_advance()
    {
        await using var h = new Harness(slides: [new(0, 1, "Long", string.Join(" ", Enumerable.Repeat("Sentence of narration.", 40)), null), BaseSlides[1]], chunkChars: 300);
        await h.StartNarrating();
        await h.AskStart();
        var before = h.S.Sent.Count;

        await h.MicSpeech();
        await h.Advance(TimeSpan.FromSeconds(10));
        await h.MicSpeech();
        await h.Advance(TimeSpan.FromSeconds(89));
        Assert.Equal(before, h.S.Sent.Count);
        Assert.Equal("listening", h.AskStates[^1].State);

        await h.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("quiet_sent", h.AskStates.Single(s => s.State == "answering").Reason);
        Assert.DoesNotContain(h.S.Sent.Skip(before), s => s.Type == "instructions");
        Assert.Equal(0, h.Presenter.Snapshot().SlideIndex);
    }

    // ---- AC2 ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ask_done_unmutes_then_queues_the_compressed_burst_in_order()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();
        var frames = new List<byte[]>();
        for (var i = 0; i < 150; i++) frames.Add(Pcm(i % 50 < 30 ? 1000 + i : i % 7));
        foreach (var frame in frames) Assert.True(await h.Presenter.SendAudioAsync(frame));
        var expected = new AskRecorder();
        foreach (var frame in frames) expected.Append(frame);
        var chunks = expected.Complete();

        var before = h.S.Sent.Count;
        Assert.True(await h.Presenter.AskDoneAsync());
        await h.Settle();

        var sent = h.S.Sent.Skip(before).Select(s => s.Type).ToList();
        Assert.Equal(["unmute", .. Enumerable.Repeat("audio", chunks.Count + 1)], sent);
        Assert.Equal(new byte[Presenter.AskLeadInMs * AskRecorder.BytesPerMs], h.S.SentAudio[0]);
        Assert.Equal(chunks.Select(c => c.ToArray()), h.S.SentAudio.Skip(1));
        Assert.Equal(new PresenterAskState("answering", 0, null, null, true, true, "sent"), h.AskStates[^1]);
        Assert.Contains(h.Logs, l => l.StartsWith("ask: sent (sent) — recorded 3.0 s, kept", StringComparison.Ordinal));
        Assert.Equal("presenting", h.Presenter.Snapshot().State);

        // Variant A: no response.create, before or after answer audio.
        await h.Advance(TimeSpan.FromSeconds(3));
        await h.Answer();
        await h.Advance(TimeSpan.FromSeconds(3));
        Assert.DoesNotContain(h.S.Sent, s => s.Type == "continue_responses");
    }

    [Fact]
    public async Task Burst_is_not_queued_before_the_unmuted_ack()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSpeech();
        h.S.AutoAckUnmute = false;

        Assert.True(await h.Presenter.AskDoneAsync());
        await h.Settle();
        Assert.Equal("unmute", h.S.Sent[^1].Type);
        // The loop is free meanwhile: other events and commands are processed, and nothing is appended.
        h.S.Hear("late", 0, 100);
        Assert.False(await h.Presenter.SendAudioAsync(AudioLevelTests.VoicedFrame(960)));
        Assert.False(await h.Presenter.AskExtendAsync());
        await h.Advance(TimeSpan.FromMilliseconds(1500));
        Assert.DoesNotContain(h.S.Sent, s => s.Type == "audio");
        Assert.DoesNotContain(h.AskStates, s => s.State == "answering");

        h.S.RaiseInputAudioUnmuted();
        await h.Settle();
        Assert.Contains(h.S.Sent, s => s.Type == "audio");
        Assert.Equal("answering", h.AskStates[^1].State);
    }

    [Fact]
    public async Task Ack_timeout_after_2_s_logs_and_sends_the_burst_once()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSpeech();
        h.S.AutoAckUnmute = false;
        Assert.True(await h.Presenter.AskDoneAsync());
        await h.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.Empty(h.S.SentAudio);

        await h.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Contains("ask: unmute ack timed out after 2000 ms; sending", h.Logs);
        var audio = h.S.SentAudio.Count;
        Assert.True(audio > 1);

        h.S.RaiseInputAudioUnmuted();
        await h.Settle();
        Assert.Equal(audio, h.S.SentAudio.Count);
        Assert.Single(h.AskStates, s => s.State == "answering");
    }

    [Theory]
    [InlineData("end")]
    [InlineData("max")]
    [InlineData("disconnect")]
    public async Task End_max_length_or_disconnect_during_the_ack_wait_sends_nothing(string how)
    {
        await using var h = new Harness(settings: Settings(maxMinutes: 5));
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSpeech();
        await h.Listen(how == "max" ? TimeSpan.FromSeconds(298) : TimeSpan.Zero);
        h.S.AutoAckUnmute = false;
        Assert.True(await h.Presenter.AskDoneAsync());
        await h.Settle();

        switch (how)
        {
            case "end": Assert.True(await h.Presenter.EndAsync()); break;
            case "disconnect": Assert.True(await h.Presenter.EndAsync(EndReasons.Disconnect)); break;
            default: await h.Advance(TimeSpan.FromSeconds(3)); break;
        }

        await h.Settle();
        Assert.Single(h.Closed);
        h.S.RaiseInputAudioUnmuted();
        await h.Advance(TimeSpan.FromSeconds(3));

        Assert.Empty(h.S.SentAudio);
        Assert.Equal("ended", Assert.Single(h.Offs).Reason);
    }

    [Theory]
    [InlineData("resume", "resumed")]
    [InlineData("next", "navigated")]
    [InlineData("cancel", "cancelled")]
    public async Task Resume_navigation_or_cancel_during_the_ack_wait_discards_the_question(string command, string reason)
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSpeech();
        h.S.AutoAckUnmute = false;
        Assert.True(await h.Presenter.AskDoneAsync());
        await h.Settle();

        Assert.True(command switch
        {
            "resume" => await h.Presenter.ResumeAsync(),
            "next" => await h.Presenter.NextAsync(),
            _ => await h.Presenter.AskCancelAsync()
        });
        await h.Settle();
        h.S.RaiseInputAudioUnmuted();
        await h.Advance(TimeSpan.FromSeconds(3));

        Assert.Empty(h.S.SentAudio);
        Assert.Equal(reason, Assert.Single(h.Offs).Reason);
        Assert.Single(h.S.Sent, s => s.Type == "unmute");
    }

    [Fact]
    public async Task Answer_audio_enters_answering_despite_skewed_timestamps()
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        h.S.Hear("the question", 90_000, 95_000);
        await h.Settle();

        h.S.Speak(200, 0, 5);
        await h.Settle();

        Assert.Contains(h.Logs, l => l.StartsWith("question: answered after", StringComparison.Ordinal));
        await h.Advance(TimeSpan.FromMilliseconds(701));
        Assert.Contains("ask: check-in", h.Logs);
    }

    [Fact]
    public async Task Burst_transcript_next_slide_is_not_a_command()
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();

        h.S.Hear("next slide", 0, 100);
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(701));

        Assert.Equal(0, h.Presenter.Snapshot().SlideIndex);
        Assert.Empty(h.Offs);
        Assert.DoesNotContain(h.Logs, l => l.StartsWith("voice:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Second_ask_done_sends_nothing()
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        var sent = h.S.Sent.Count;

        Assert.False(await h.Presenter.AskDoneAsync());
        await h.Settle();

        Assert.Equal(sent, h.S.Sent.Count);
        Assert.Single(h.AskStates, s => s.State == "answering");
    }

    [Fact]
    public async Task Append_refused_at_chunk_n_reports_send_failed_not_sent_and_resets_the_upstream()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        var first = h.S;
        first.RefuseAudioFromChunk = 3;
        await h.AskStart();
        await h.MicSpeech();

        await h.AskDone();

        Assert.Equal("send_failed", Assert.Single(h.Offs).Reason);
        Assert.DoesNotContain(h.AskStates, s => s.State == "answering");
        Assert.Contains(h.Logs, l => l.StartsWith("ask: send failed at chunk 4/", StringComparison.Ordinal));
        await Eventually(() => first.DisposeCount == 1);
        await h.Settle();
        Assert.Empty(h.Closed);
        var snapshot = h.Presenter.Snapshot();
        Assert.Equal("paused", snapshot.State);
        Assert.True(snapshot.Suspended);

        await h.AskStart();
        Assert.Equal(2, h.Sessions.Count);
        Assert.Equal("mute", h.S.Sent.Single(s => s.Type == "mute").Type);
        Assert.Equal("listening", h.AskStates[^1].State);
    }

    // ---- AC3 ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Answer_then_check_in_then_resume_instruction()
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        await h.Answer();
        await h.Advance(TimeSpan.FromMilliseconds(701));
        Assert.Contains("ask: check-in", h.Logs);
        Assert.Empty(h.Offs);

        await h.Advance(TimeSpan.FromMilliseconds(Presenter.DefaultFollowUpWaitMs));

        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
        var resume = Assert.Single(h.S.Sent, IsResumeAfterQuestion);
        Assert.Equal(PromptBuilder.ResumeAfterAskInstruction(0, 3, "One", followUp: false), resume.Content);
        Assert.Contains("ask: exchange ended (resume), 0 deferred notices, replay no", h.Logs);
    }

    [Fact]
    public async Task Quiet_90_s_after_speech_sends_with_quiet_sent()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSpeech();

        await h.Advance(TimeSpan.FromSeconds(89));
        Assert.Empty(h.S.SentAudio);
        await h.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("quiet_sent", h.AskStates.Single(s => s.State == "answering").Reason);
        Assert.NotEmpty(h.S.SentAudio);
    }

    [Fact]
    public async Task Quiet_90_s_without_speech_cancels_unmutes_and_stays_paused_with_grace_rearmed()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSilence(1000);

        await h.Advance(TimeSpan.FromSeconds(90));

        Assert.Equal("quiet_cancelled", Assert.Single(h.Offs).Reason);
        Assert.Equal("unmute", h.S.Sent[^1].Type);
        Assert.Equal("paused", h.Presenter.Snapshot().State);
        Assert.Contains("ask: cancelled (quiet_cancelled)", h.Logs);
        await h.Advance(TimeSpan.FromSeconds(119));
        Assert.DoesNotContain(h.S.Sent, s => s.Type == "close");
        await h.Advance(TimeSpan.FromSeconds(1));
        Assert.Contains(h.S.Sent, s => s.Type == "close");
        Assert.True(h.Presenter.Snapshot().Suspended);
    }

    [Fact]
    public async Task Extend_at_80_s_keeps_listening_until_170_s()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSpeech();
        await h.Advance(TimeSpan.FromSeconds(80));

        Assert.True(await h.Presenter.AskExtendAsync());
        await h.Settle();
        Assert.Equal(Presenter.AskQuietTimeoutMs, h.AskStates[^1].QuietRemainingMs);
        Assert.Contains("ask: extended", h.Logs);
        await h.Advance(TimeSpan.FromSeconds(89));
        Assert.DoesNotContain(h.AskStates, s => s.State != "listening");

        await h.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("quiet_sent", h.AskStates.Single(s => s.State == "answering").Reason);
    }

    [Fact]
    public async Task Ticks_emit_elapsed_and_quiet_remaining_every_second()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSpeech(500);

        for (var i = 0; i < 3; i++) await h.Advance(TimeSpan.FromSeconds(1));

        var ticks = h.AskStates.Skip(1).ToList();
        Assert.Equal([1000L, 2000L, 3000L], ticks.Select(t => t.ElapsedMs));
        Assert.Equal([89_000L, 88_000L, 87_000L], ticks.Select(t => t.QuietRemainingMs!.Value));
        Assert.All(ticks, t => Assert.True(t.Heard));
        Assert.All(ticks, t => Assert.Equal("listening", t.State));
    }

    [Fact]
    public async Task Speech_cap_of_25_s_sends_with_limit_sent_and_silence_does_not_count()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSpeech(10_000);
        await h.MicSilence(30_000);
        await h.Advance(TimeSpan.FromSeconds(1));
        var remaining = h.AskStates[^1].SpeechRemainingMs!.Value;
        Assert.InRange(remaining, 14_000, 15_000);

        await h.MicSpeech(14_000);
        await h.Advance(TimeSpan.FromSeconds(1));
        Assert.True(h.AskStates[^1].SpeechRemainingMs < 1_500);
        Assert.Empty(h.S.SentAudio);
        await h.MicSpeech(2_000);

        Assert.Equal("limit_sent", h.AskStates.Single(s => s.State == "answering").Reason);
        Assert.Contains("ask: speech limit of 25 s reached", h.Logs);
        // After the burst the live mic is forwarded again (P-16); only the burst counts here.
        var answeringAt = h.Marks.First(m => m.Kind.StartsWith("ask:answering", StringComparison.Ordinal)).SentCount;
        var burst = h.S.Sent.Take(answeringAt).Count(s => s.Type == "audio");
        var kept = h.S.SentAudio.Take(burst).Skip(1).Sum(a => a.Length) / AskRecorder.BytesPerMs;
        Assert.Equal(AskRecorder.MaxRetainedMs + AskRecorder.DefaultTailSilenceMs, kept);
    }

    [Fact]
    public async Task Ask_done_without_speech_is_empty_and_stays_paused()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSilence(2000);

        Assert.False(await h.Presenter.AskDoneAsync());
        await h.Settle();

        Assert.Equal("empty", Assert.Single(h.Offs).Reason);
        Assert.Empty(h.S.SentAudio);
        Assert.Equal("paused", h.Presenter.Snapshot().State);
        Assert.Equal("unmute", h.S.Sent[^1].Type);
    }

    // ---- AC4, atomic start ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Ask_just_before_grace_expiry_with_the_guard_event_already_queued_does_not_suspend()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        Assert.True(await h.Presenter.PauseAsync());
        await h.Advance(TimeSpan.FromSeconds(119));
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        h.Service.BeforeSnapshot = _ =>
        {
            h.Service.BeforeSnapshot = null;
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };
        h.Service.RaiseChanged(Pid);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        var ask = h.Presenter.AskStartAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        release.Set();
        Assert.True(await ask);
        await h.Settle();

        Assert.DoesNotContain(h.S.Sent, s => s.Type == "close");
        Assert.False(h.Presenter.Snapshot().Suspended);
        await h.Listen(TimeSpan.FromSeconds(240));
        Assert.DoesNotContain(h.S.Sent, s => s.Type == "close");
        Assert.Equal("listening", h.AskStates[^1].State);
    }

    [Fact]
    public async Task Ask_after_the_grace_fired_reconnects_rechecks_and_listens()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        Assert.True(await h.Presenter.PauseAsync());
        await h.Advance(TimeSpan.FromSeconds(121));
        Assert.True(h.Presenter.Snapshot().Suspended);

        await h.AskStart();

        Assert.Equal(2, h.Sessions.Count);
        Assert.Contains(h.S.Sent, s => s.Type == "mute");
        Assert.False(h.Presenter.Snapshot().Suspended);
        Assert.Equal("listening", Assert.Single(h.AskStates).State);
        Assert.Contains("ask: listening (from paused)", h.Logs);
    }

    [Fact]
    public async Task Reconnect_failure_at_ask_start_ends_the_talk_and_leaves_no_exchange()
    {
        await using var h = new Harness(configure: (session, index) => session.FailConnect = index > 0);
        await h.StartNarrating();
        Assert.True(await h.Presenter.PauseAsync());
        await h.Advance(TimeSpan.FromSeconds(121));

        Assert.False(await h.Presenter.AskStartAsync());
        await h.Settle();

        Assert.Equal(EndReasons.ReconnectFailed, Assert.Single(h.Closed).EndReason);
        Assert.Equal("refused_not_live", Assert.Single(h.Offs).Reason);
        Assert.DoesNotContain(h.AskStates, s => s.State == "listening");
        Assert.False(await h.Presenter.AskDoneAsync());
        Assert.Equal("idle", h.Presenter.Snapshot().State);
    }

    [Fact]
    public async Task Mute_refused_at_ask_start_rolls_back_grace_and_emits_unavailable()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        Assert.True(await h.Presenter.PauseAsync());
        await h.Advance(TimeSpan.FromSeconds(100));
        h.S.RefuseMute = true;

        Assert.False(await h.Presenter.AskStartAsync());
        await h.Settle();

        Assert.Equal("unavailable", Assert.Single(h.Offs).Reason);
        Assert.DoesNotContain(h.S.Sent, s => s.Type == "mute");
        // The grace was re-armed from the refused start: it fires 120 s later, not at 120 s of the pause.
        await h.Advance(TimeSpan.FromSeconds(119));
        Assert.False(h.Presenter.Snapshot().Suspended);
        await h.Advance(TimeSpan.FromSeconds(1));
        Assert.True(h.Presenter.Snapshot().Suspended);
    }

    [Fact]
    public async Task Transcriber_begin_throwing_rolls_back_unmutes_and_rearms_grace()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        Assert.True(await h.Presenter.PauseAsync());
        h.Transcriber.ThrowOnBegin = new InvalidOperationException("no transcriber");
        var before = h.S.Sent.Count;

        Assert.False(await h.Presenter.AskStartAsync());
        await h.Settle();

        Assert.Equal(["mute", "unmute"], h.S.Sent.Skip(before).Select(s => s.Type));
        Assert.Equal("unavailable", Assert.Single(h.Offs).Reason);
        Assert.Contains("ask: transcriber failed to begin (InvalidOperationException)", h.Logs);
        await h.Advance(TimeSpan.FromSeconds(120));
        Assert.True(h.Presenter.Snapshot().Suspended);
    }

    [Fact]
    public async Task Unmute_failing_during_rollback_resets_the_upstream()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        var first = h.S;
        first.RefuseUnmute = true;
        h.Transcriber.ThrowOnBegin = new InvalidOperationException("no transcriber");

        Assert.False(await h.Presenter.AskStartAsync());
        await Eventually(() => first.DisposeCount == 1);
        await h.Settle();

        Assert.Equal("unavailable", Assert.Single(h.Offs).Reason);
        Assert.Contains(first.Sent, s => s.Type == "close");
        Assert.True(h.Presenter.Snapshot().Suspended);
        Assert.Equal("paused", h.Presenter.Snapshot().State);
        Assert.Empty(h.Closed);

        Assert.True(await h.Presenter.ResumeAsync());
        await h.Settle();
        Assert.Equal(2, h.Sessions.Count);
        Assert.Equal("presenting", h.Presenter.Snapshot().State);
    }

    // ---- AC4, other -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ask_from_paused_answers_and_checks_in()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        Assert.True(await h.Presenter.PauseAsync());
        await h.AskStart();
        Assert.Contains("ask: listening (from paused)", h.Logs);
        await h.MicSpeech();
        await h.AskDone();
        Assert.Equal("presenting", h.Presenter.Snapshot().State);
        // A real answer cannot arrive before the burst is ingested (T8: at least 1.27 s).
        await h.Advance(TimeSpan.FromMilliseconds(Presenter.ResidualStartMs));

        await h.Answer();
        var forwarded = h.Forwarded;
        await h.Answer();
        Assert.True(h.Forwarded > forwarded);
        await h.Advance(TimeSpan.FromMilliseconds(701));
        Assert.Contains("ask: check-in", h.Logs);
        await h.Reply("yes");
        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
    }

    [Fact]
    public async Task Pause_grace_does_not_suspend_during_a_long_ask()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        Assert.True(await h.Presenter.PauseAsync());
        await h.Advance(TimeSpan.FromSeconds(60));
        await h.AskStart();
        await h.MicSpeech();

        await h.Listen(TimeSpan.FromSeconds(300));

        Assert.DoesNotContain(h.S.Sent, s => s.Type == "close");
        Assert.False(h.Presenter.Snapshot().Suspended);
        Assert.Equal("listening", h.AskStates[^1].State);
    }

    [Fact]
    public async Task Idle_guard_does_not_end_a_talk_during_a_five_minute_ask()
    {
        await using var h = new Harness(settings: Settings(idleSeconds: 120));
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSpeech();

        await h.Listen(TimeSpan.FromMinutes(5));

        Assert.Empty(h.Closed);
        Assert.DoesNotContain(h.Warnings, w => w.Kind == EndReasons.Idle && w.SecondsLeft is not null);
        Assert.Equal("listening", h.AskStates[^1].State);
    }

    [Fact]
    public async Task Max_length_during_an_ask_ends_the_talk_without_a_burst()
    {
        await using var h = new Harness(settings: Settings(maxMinutes: 5));
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSpeech();

        await h.Listen(TimeSpan.FromMinutes(5));

        Assert.Equal(EndReasons.MaxLength, Assert.Single(h.Closed).EndReason);
        Assert.Equal("ended", Assert.Single(h.Offs).Reason);
        Assert.Empty(h.S.SentAudio);
        Assert.DoesNotContain(h.S.Sent, s => s.Type == "unmute");
    }

    [Fact]
    public async Task End_during_an_ask_emits_off_ended_before_closed_and_sends_no_unmute_or_audio()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSpeech();

        Assert.True(await h.Presenter.EndAsync());
        await h.Settle();

        var off = h.Marks.FindIndex(m => m.Kind == "ask:off:ended");
        Assert.True(off >= 0 && off < h.Marks.FindIndex(m => m.Kind == "closed"));
        Assert.DoesNotContain(h.S.Sent, s => s.Type is "unmute" or "audio");
        Assert.Contains("ask: exchange ended (ended), 0 deferred notices, replay no", h.Logs);
    }

    [Fact]
    public async Task Upstream_loss_during_the_answer_ends_the_exchange()
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        await h.Answer();

        h.S.Drop();
        await h.Settle();

        Assert.Equal(EndReasons.UpstreamLost, Assert.Single(h.Closed).EndReason);
        Assert.Equal("ended", Assert.Single(h.Offs).Reason);
        Assert.False(await h.Presenter.AskDoneAsync());
    }

    [Theory]
    [InlineData("resume", "resumed", "presenting")]
    [InlineData("next", "navigated", "presenting")]
    [InlineData("mute", "muted", "paused")]
    public async Task User_resume_navigation_or_mute_end_the_exchange_first(string command, string reason, string state)
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSpeech();
        var offAt = -1;
        h.OnAsk = s => { if (s.State == "off") offAt = h.S.Sent.Count; };

        Assert.True(command switch
        {
            "resume" => await h.Presenter.ResumeAsync(),
            "next" => await h.Presenter.NextAsync(),
            _ => await h.Presenter.MuteAsync()
        });
        await h.Settle();

        Assert.Equal(reason, Assert.Single(h.Offs).Reason);
        Assert.Equal(state, h.Presenter.Snapshot().State);
        Assert.Contains($"ask: cancelled ({reason})", h.Logs);
        Assert.Empty(h.S.SentAudio);
        var after = h.S.Sent.Skip(offAt).ToList();
        switch (command)
        {
            case "resume":
                Assert.Contains(after, s => s.EventId == "resume-1");
                Assert.Equal("unmute", h.S.Sent[offAt - 1].Type);
                break;
            case "next":
                Assert.Contains(after, s => s.EventId == "slide-2-part-1");
                break;
            default:
                Assert.True(h.Presenter.Snapshot().Muted);
                Assert.DoesNotContain(h.S.Sent, s => s.Type == "unmute");
                break;
        }
    }

    [Fact]
    public async Task Refused_while_muted_or_not_live()
    {
        await using var h = new Harness();
        Assert.False(await h.Presenter.AskStartAsync());
        await h.StartNarrating();
        Assert.True(await h.Presenter.MuteAsync());
        Assert.False(await h.Presenter.AskStartAsync());
        await h.Settle();

        Assert.Equal(["refused_not_live", "refused_muted"], h.Offs.Select(o => o.Reason));
        Assert.Single(h.S.Sent, s => s.Type == "mute");
        Assert.Equal("presenting", h.Presenter.Snapshot().State);
    }

    [Fact]
    public async Task Second_ask_start_while_listening_is_idempotent()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSpeech();

        await h.AskStart();

        Assert.Single(h.S.Sent, s => s.Type == "mute");
        Assert.Single(h.S.Sent, s => s.EventId == "pause-1");
        Assert.Equal(2, h.AskStates.Count(s => s.State == "listening"));
        Assert.True(h.AskStates[^1].Heard);
        Assert.Single(h.Transcriber.Transcriptions);
    }

    [Theory]
    [InlineData("answering")]
    [InlineData("check-in")]
    public async Task Follow_up_ask_during_the_answer_flushes_listens_again_and_resumes_the_first_interrupted_sentence(string phase)
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        await h.Answer();
        if (phase == "check-in") await h.Advance(TimeSpan.FromMilliseconds(701));
        var flushes = h.Flushes;

        await h.AskStart();

        Assert.Empty(h.Offs);
        Assert.Equal(flushes + 1, h.Flushes);
        Assert.Equal("listening", h.AskStates[^1].State);
        Assert.Equal("paused", h.Presenter.Snapshot().State);
        Assert.Equal(2, h.S.Sent.Count(s => s.Type == "mute"));
        Assert.Contains("ask: listening (from answer, follow-up)", h.Logs);
        var before = h.S.Sent.Count;
        await h.MicSpeech();
        await h.AskDone();
        Assert.Contains(h.S.Sent.Skip(before), s => s.Type == "audio");
        // A real answer cannot arrive before the burst is ingested (T8: at least 1.27 s).
        await h.Advance(TimeSpan.FromMilliseconds(Presenter.ResidualStartMs));
        await h.Answer();
        await h.Advance(TimeSpan.FromMilliseconds(701));
        await h.Advance(TimeSpan.FromMilliseconds(Presenter.DefaultFollowUpWaitMs));

        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
        var resume = Assert.Single(h.S.Sent, IsResumeAfterQuestion);
        Assert.Equal(PromptBuilder.ResumeAfterAskInstruction(0, 3, "One", followUp: true), resume.Content);
        Assert.Equal(0, h.Presenter.Snapshot().SlideIndex);
        Assert.Equal("presenting", h.Presenter.Snapshot().State);
    }

    [Fact]
    public async Task Follow_up_ask_keeps_the_resume_point_of_the_first_ask_made_before_narration_was_heard()
    {
        await using var h = new Harness();
        await h.Start();
        await h.AskStart();
        await h.MicSpeech();
        await h.AskDone();
        // The first answer is voiced: answer audio is output, but it is not the slide's narration.
        await h.Answer();

        await h.AskStart();
        await h.MicSpeech();
        await h.AskDone();
        // A real answer cannot arrive before the burst is ingested (T8: at least 1.27 s).
        await h.Advance(TimeSpan.FromMilliseconds(Presenter.ResidualStartMs));
        await h.Answer();
        await h.Advance(TimeSpan.FromMilliseconds(701));
        await h.Advance(TimeSpan.FromMilliseconds(Presenter.DefaultFollowUpWaitMs));

        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
        Assert.DoesNotContain(h.S.Sent, IsResumeAfterQuestion);
        Assert.Contains("Resume slide", Assert.Single(h.S.Sent, s => s.EventId == "resume-1").Content);
    }

    [Fact]
    public async Task Next_start_after_an_ended_ask_is_clean()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSpeech();
        Assert.True(await h.Presenter.EndAsync());
        await h.Settle();

        await h.StartNarrating();
        Assert.True(await h.Presenter.SendAudioAsync(AudioLevelTests.VoicedFrame(960)));
        Assert.True(await h.Presenter.UnmuteAsync());
        await h.Advance(TimeSpan.FromSeconds(100));

        Assert.Single(h.Offs);
        Assert.Single(h.S.Sent, s => s.Type == "audio");
        Assert.DoesNotContain(h.S.Sent, s => s.Type == "mute");
        Assert.False(await h.Presenter.AskDoneAsync());
    }

    // ---- AC5 ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Disabled_transcriber_reports_not_transcribing_and_never_finishes_an_ask()
    {
        await using var h = new Harness(transcriber: new DisabledAskTranscriber());
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSpeech(3000);
        await h.Advance(TimeSpan.FromSeconds(60));

        Assert.All(h.AskStates, s => Assert.False(s.Transcribing));
        Assert.All(h.AskStates, s => Assert.Equal("listening", s.State));
    }

    [Theory]
    [InlineData("How much did the Hanoi expansion cost, ask done", "en")]
    [InlineData("Chi phí mở rộng Hà Nội là bao nhiêu, tôi hỏi xong rồi", "vi")]
    public async Task Test_transcriber_phrase_finishes_after_700_ms_without_a_newer_update(string text, string language)
    {
        var question = AskDoneLexicon.Match(text)!.Question;
        Assert.DoesNotContain("xong", question, StringComparison.Ordinal);
        Assert.DoesNotContain("done", question, StringComparison.Ordinal);
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSpeech();

        await h.Transcriber.PublishAsync(1, text);
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(699));
        Assert.DoesNotContain(h.AskStates, s => s.State == "answering");

        await h.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("phrase_sent", h.AskStates.Single(s => s.State == "answering").Reason);
        Assert.Contains($"ask: done by phrase ({language}), question {question.Length} chars", h.Logs);
        Assert.DoesNotContain(h.Logs, l => l.Contains("Hanoi", StringComparison.Ordinal) || l.Contains("Hà Nội", StringComparison.Ordinal));
        Assert.True(h.Transcriber.Current!.Disposed);
    }

    [Fact]
    public async Task Phrase_followed_by_trailing_words_within_700_ms_does_not_finish()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSpeech();

        await h.Transcriber.PublishAsync(1, "What does it cost, ask done");
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(400));
        await h.Transcriber.PublishAsync(2, "What does it cost, ask done and also where");
        await h.Settle();
        await h.Advance(TimeSpan.FromSeconds(2));

        Assert.All(h.AskStates, s => Assert.Equal("listening", s.State));
    }

    [Fact]
    public async Task Final_update_with_the_phrase_finishes_at_once_even_after_700_ms_of_audio_quiet()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSpeech();
        await h.MicSilence(1000);

        await h.Transcriber.PublishAsync(1, "What does it cost? That's my question.", final: true);
        await h.Settle();

        Assert.Equal("phrase_sent", h.AskStates.Single(s => s.State == "answering").Reason);
    }

    [Fact]
    public async Task Stale_duplicate_or_reversed_revisions_are_ignored()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSpeech();

        await h.Transcriber.PublishAsync(3, "What does it cost");
        await h.Settle();
        await h.Transcriber.PublishAsync(2, "What does it cost, ask done", final: true);
        await h.Transcriber.PublishAsync(3, "What does it cost, over to you", final: true);
        await h.Settle();
        await h.Advance(TimeSpan.FromSeconds(1));
        Assert.All(h.AskStates, s => Assert.Equal("listening", s.State));

        await h.Transcriber.PublishAsync(4, "What does it cost, over to you", final: true);
        await h.Settle();
        Assert.Equal("phrase_sent", h.AskStates.Single(s => s.State == "answering").Reason);
    }

    [Fact]
    public async Task Update_of_a_previous_ask_is_ignored()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();
        Assert.True(await h.Presenter.AskCancelAsync());
        await h.AskStart();
        await h.MicSpeech();

        await h.Transcriber.Transcriptions[0].PublishAsync(9, "What does it cost, ask done", final: true);
        await h.Settle();
        await h.Advance(TimeSpan.FromSeconds(1));

        Assert.DoesNotContain(h.AskStates, s => s.State == "answering");
        Assert.Equal("listening", h.AskStates[^1].State);
    }

    // ---- Review r1: ack correlation (#1) ---------------------------------------------------------------------------

    [Theory]
    [InlineData("timeout", true)]
    [InlineData("timeout", false)]
    [InlineData("cancel", true)]
    [InlineData("cancel", false)]
    public async Task Late_ack_of_an_earlier_ask_does_not_release_the_next_burst(string firstEnds, bool echoId)
    {
        await using var h = new Harness();
        await h.StartNarrating();
        h.S.AutoAckUnmute = false;
        h.S.EchoUnmuteId = echoId;
        await h.AskStart();
        await h.MicSpeech();
        await h.AskDone();
        if (firstEnds == "timeout")
        {
            await h.Advance(TimeSpan.FromMilliseconds(Presenter.AskUnmuteAckTimeoutMs));
            Assert.Contains("ask: unmute ack timed out after 2000 ms; sending", h.Logs);
        }
        else
        {
            Assert.True(await h.Presenter.AskCancelAsync());
            await h.Settle();
        }

        await h.AskStart();
        await h.MicSpeech();
        await h.AskDone();
        Assert.Equal(2, h.S.UnmuteIds.Count);
        var audio = h.S.SentAudio.Count;
        var answering = h.AskStates.Count(a => a.State == "answering");

        h.S.RaiseInputAudioUnmuted(echoId ? h.S.UnmuteIds[0] : null);
        await h.Settle();
        Assert.Equal(audio, h.S.SentAudio.Count);
        Assert.Contains("ask: unmute ack of an earlier unmute ignored", h.Logs);

        h.S.RaiseInputAudioUnmuted(echoId ? h.S.UnmuteIds[1] : null);
        await h.Settle();
        Assert.True(h.S.SentAudio.Count > audio);
        Assert.Equal(answering + 1, h.AskStates.Count(a => a.State == "answering"));
        var burst = h.S.SentAudio.Count;
        h.S.RaiseInputAudioUnmuted(echoId ? h.S.UnmuteIds[1] : null);
        await h.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(burst, h.S.SentAudio.Count);
    }

    // ---- Review r1: notices across the send-failure reset (#2) ------------------------------------------------------

    [Theory]
    [InlineData("edit pending")]
    [InlineData("edit failed")]
    [InlineData("edit declined")]
    [InlineData("limit warning")]
    public async Task Deferred_notice_survives_a_send_failure_reset_and_is_appended_once_after_reconnect(string notice)
    {
        await using var h = new Harness(settings: Settings(maxMinutes: 5));
        await h.StartNarrating();
        await h.TrainerOn();
        var first = h.S;
        first.RefuseAudioFromChunk = 2;
        string content;
        if (notice == "edit declined")
        {
            Assert.True(await h.Presenter.PauseAsync());
            h.S.RaiseToolCall("d1", "c1", "revise_script", "{\"feedback\":\"Shorter.\"}");
            await Eventually(() => h.S.Sent.Any(s => s.EventId == "c1"));
            await h.Settle();
        }

        await h.AskStart();
        await h.MicSpeech();
        var askedAt = first.Sent.Count;
        switch (notice)
        {
            case "edit pending":
                await h.TrainOn(1);
                content = PromptBuilder.ScriptEditPendingInstruction();
                break;
            case "edit failed":
                var id = await h.TrainOn(1);
                Assert.True(h.Service.SetOutcome(id, EditOutcome.Failed([1], ScriptEditErrors.Cancelled)));
                h.Service.RaiseChanged(Pid);
                await h.Settle();
                content = PromptBuilder.ScriptEditFailedInstruction();
                break;
            case "edit declined":
                await h.Advance(TimeSpan.FromSeconds(8));
                await h.Advance(TimeSpan.FromSeconds(10));
                content = PromptBuilder.ScriptEditDeclinedInstruction();
                break;
            default:
                await h.Listen(TimeSpan.FromSeconds(241));
                Assert.Contains(h.Warnings, w => w.Kind == EndReasons.MaxLength && w.SecondsLeft == 60);
                content = PromptBuilder.LimitWarningInstruction(EndReasons.MaxLength);
                break;
        }

        Assert.DoesNotContain(first.Sent.Skip(askedAt), s => s.Content == content);
        await h.AskDone();
        Assert.Equal("send_failed", Assert.Single(h.Offs).Reason);
        await Eventually(() => first.DisposeCount == 1);
        await h.Settle();
        Assert.DoesNotContain(first.Sent.Skip(askedAt), s => s.Content == content);

        Assert.True(await h.Presenter.ResumeAsync());
        await h.Settle();
        Assert.Equal(2, h.Sessions.Count);
        Assert.Single(h.S.Sent, s => s.Content == content);
        Assert.True(await h.Presenter.PauseAsync());
        Assert.True(await h.Presenter.ResumeAsync());
        await h.Settle();
        Assert.Single(h.S.Sent, s => s.Content == content);
    }

    // ---- Review r1: voice confirmation during the answer (#3) -------------------------------------------------------

    [Theory]
    [InlineData("yes")]
    [InlineData("no")]
    public async Task Spoken_reply_settles_a_tool_confirmation_during_the_answer(string reply)
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.TrainerOn();
        await h.ToAwaitingAnswer(start: false);
        await h.Answer();
        h.S.RaiseToolCall("d1", "c1", "revise_script", "{\"feedback\":\"Shorter.\"}");
        await Eventually(() => h.S.Sent.Any(s => s.EventId == "c1"));
        await h.Settle();
        await h.Advance(TimeSpan.FromSeconds(2));

        await h.Reply(reply);

        if (reply == "yes")
        {
            Assert.Single(h.Service.Enqueued);
            Assert.Contains("ask: tool confirmation answered yes", h.Logs);
        }
        else
        {
            Assert.Empty(h.Service.Enqueued);
            Assert.Contains(h.Logs, l => l.EndsWith("revise_script declined", StringComparison.Ordinal));
            Assert.DoesNotContain(h.S.Sent, s => s.Content == PromptBuilder.ScriptEditDeclinedInstruction());
        }

        Assert.Equal(0, h.Presenter.Snapshot().SlideIndex);
        // The answer's check-in and timeout still end the exchange, once.
        await h.Advance(TimeSpan.FromMilliseconds(701));
        await h.Advance(TimeSpan.FromMilliseconds(Presenter.DefaultFollowUpWaitMs + 701));
        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
        if (reply == "no") Assert.Single(h.S.Sent, s => s.Content == PromptBuilder.ScriptEditDeclinedInstruction());
    }

    [Fact]
    public async Task Delayed_question_fragment_does_not_confirm_a_tool_during_the_answer()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.TrainerOn();
        await h.ToAwaitingAnswer(start: false);
        await h.Answer();
        h.S.Hear("and the rest of my question", 0, 100);
        await h.Settle();
        h.S.RaiseToolCall("d1", "c1", "revise_script", "{\"feedback\":\"Shorter.\"}");
        await Eventually(() => h.S.Sent.Any(s => s.EventId == "c1"));
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(500));

        // Within 1.5 s of the previous user delta: a late fragment of the question, not a reply.
        await h.Reply("yes");
        await h.Advance(TimeSpan.FromSeconds(8));
        await h.Advance(TimeSpan.FromSeconds(10));

        Assert.Empty(h.Service.Enqueued);
        Assert.Contains(h.Logs, l => l.EndsWith("revise_script not confirmed", StringComparison.Ordinal));
        Assert.DoesNotContain("ask: tool confirmation answered yes", h.Logs);
    }

    [Fact]
    public async Task Navigation_words_during_a_tool_confirmation_in_the_answer_do_not_navigate()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.TrainerOn();
        await h.ToAwaitingAnswer(start: false);
        await h.Answer();
        h.S.RaiseToolCall("d1", "c1", "revise_script", "{\"feedback\":\"Shorter.\"}");
        await Eventually(() => h.S.Sent.Any(s => s.EventId == "c1"));
        await h.Settle();

        await h.Reply("next slide");

        Assert.Equal(0, h.Presenter.Snapshot().SlideIndex);
        Assert.Contains("ask: unclear confirmation reply; left to the timeout", h.Logs);
        Assert.Empty(h.Service.Enqueued);
    }

    // ---- Review r1: single disposal (#4) ----------------------------------------------------------------------------

    [Theory]
    [InlineData("done")]
    [InlineData("empty")]
    [InlineData("quiet")]
    [InlineData("cancel")]
    [InlineData("end")]
    [InlineData("follow-up")]
    public async Task Each_transcription_is_disposed_exactly_once(string how)
    {
        // TestAskTranscription throws on a second Dispose; a throw on the loop would close the talk.
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();
        if (how is not ("empty" or "quiet")) await h.MicSpeech();
        switch (how)
        {
            case "done":
            case "empty":
                await h.Presenter.AskDoneAsync();
                break;
            case "quiet":
                break;
            case "cancel":
                Assert.True(await h.Presenter.AskCancelAsync());
                break;
            case "end":
                Assert.True(await h.Presenter.EndAsync());
                break;
            default:
                await h.AskDone();
                await h.Answer();
                await h.AskStart();
                await h.MicSpeech();
                await h.AskDone();
                break;
        }

        await h.Settle();
        if (how == "quiet")
        {
            await h.Advance(TimeSpan.FromSeconds(90));
        }

        if (how != "end")
        {
            Assert.True(await h.Presenter.EndAsync());
        }

        await h.Settle();
        Assert.All(h.Transcriber.Transcriptions, t => Assert.True(t.Disposed));
        Assert.Equal(how == "follow-up" ? 2 : 1, h.Transcriber.Transcriptions.Count);
        Assert.DoesNotContain(h.Logs, l => l.StartsWith("presenter handler failed", StringComparison.Ordinal));
        Assert.Equal(EndReasons.User, Assert.Single(h.Closed).EndReason);
    }

    // ---- Review r2: confirmation attribution (#1) ---------------------------------------------------------------

    [Theory]
    [InlineData("yes")]
    [InlineData("no")]
    public async Task Reply_begun_before_a_tool_confirmation_neither_approves_nor_declines_it(string reply)
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.TrainerOn();
        await h.ToAwaitingAnswer(start: false);
        await h.Answer();
        await h.Advance(TimeSpan.FromMilliseconds(701));
        Assert.Contains("ask: check-in", h.Logs);

        // Delta → tool request → the utterance's 700 ms debounce: the reply began before the confirmation.
        h.S.Hear(reply, 0, 100);
        await h.Settle();
        h.S.RaiseToolCall("d1", "c1", "revise_script", "{\"feedback\":\"Shorter.\"}");
        await Eventually(() => h.S.Sent.Any(s => s.EventId == "c1"));
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(701));

        Assert.Empty(h.Service.Enqueued);
        Assert.DoesNotContain(h.Logs, l => l.StartsWith("ask: tool confirmation answered", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Logs, l => l.EndsWith("revise_script declined", StringComparison.Ordinal));
        Assert.Contains("ask: open reply discarded; a tool confirmation began", h.Logs);
        Assert.Empty(h.Offs);

        // A fresh reply, after the confirmation began and after 1.5 s of transcript quiet, still answers it.
        await h.Advance(TimeSpan.FromMilliseconds(1600));
        await h.Reply(reply);
        if (reply == "yes") Assert.Single(h.Service.Enqueued);
        else Assert.Contains(h.Logs, l => l.EndsWith("revise_script declined", StringComparison.Ordinal));
    }

    // ---- Review r2: refused follow-ups (#2, #3) -----------------------------------------------------------------

    [Theory]
    [InlineData("awaiting", "mute")]
    [InlineData("awaiting", "begin")]
    [InlineData("awaiting", "rollback")]
    [InlineData("answering", "mute")]
    [InlineData("answering", "begin")]
    [InlineData("answering", "rollback")]
    [InlineData("check-in", "mute")]
    [InlineData("check-in", "begin")]
    [InlineData("check-in", "rollback")]
    public async Task Refused_follow_up_keeps_a_live_answer_or_ends_it_once_when_the_upstream_is_reset(string phase, string failure)
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.TrainerOn();
        await h.ToAwaitingAnswer(start: false);
        if (phase != "awaiting") await h.Answer();
        if (phase == "check-in") await h.Advance(TimeSpan.FromMilliseconds(701));
        // Deferred state carried by the exchange: an edit notice and a replay due.
        await h.TrainOn(2);
        await h.Revert(6, (0, "Slide one, v6."));
        var first = h.S;
        first.CloseSeconds = 7;
        switch (failure)
        {
            case "mute": first.RefuseMute = true; break;
            case "begin": h.Transcriber.ThrowOnBegin = new InvalidOperationException("no transcriber"); break;
            default:
                h.Transcriber.ThrowOnBegin = new InvalidOperationException("no transcriber");
                first.RefuseUnmute = true;
                break;
        }

        var frames = h.AskStates.Count;
        Assert.False(await h.Presenter.AskStartAsync());
        await h.Settle();
        var after = h.AskStates.Skip(frames).Select(a => $"{a.State}:{a.Reason}").ToList();

        if (failure != "rollback")
        {
            // The answer survives on its live upstream: the refusal, then the answer re-announced.
            Assert.Equal(["off:unavailable", "answering:sent"], after);
            Assert.Equal("presenting", h.Presenter.Snapshot().State);
            first.RefuseMute = false;
            h.Transcriber.ThrowOnBegin = null;
            Assert.True(await h.Presenter.ResumeAsync());
            await h.Settle();
            Assert.Equal(["unavailable", "continued"], h.Offs.Select(o => o.Reason));
            Assert.Single(first.Sent, s => s.Content == PromptBuilder.ScriptEditPendingInstruction());
            Assert.Single(first.Sent, s => s.EventId == "slide-1-updated-v6");
            return;
        }

        // The rollback unmute was refused: the upstream is reset, so the answer exchange ends once, never re-announced.
        Assert.Equal(["off:unavailable"], after);
        await Eventually(() => first.DisposeCount == 1);
        await h.Settle();
        var snapshot = h.Presenter.Snapshot();
        Assert.Equal("paused", snapshot.State);
        Assert.True(snapshot.Suspended);
        Assert.Contains(h.Usages, u => u.Seconds == 7);
        Assert.DoesNotContain(first.Sent, s => s.Content == PromptBuilder.ScriptEditPendingInstruction());
        // The old answer's timers are gone: no later off, no resume.
        await h.Advance(TimeSpan.FromSeconds(30));
        Assert.Single(h.Offs);
        Assert.DoesNotContain(first.Sent, IsResumeAfterQuestion);
        Assert.Empty(h.Closed);

        // Recovery: Resume reconnects, delivers the kept notice once and replays the current slide once.
        h.Transcriber.ThrowOnBegin = null;
        Assert.True(await h.Presenter.ResumeAsync());
        await h.Settle();
        Assert.Equal(2, h.Sessions.Count);
        Assert.Single(h.S.Sent, s => s.Content == PromptBuilder.ScriptEditPendingInstruction());
        Assert.Single(h.S.Sent, s => s.EventId == "slide-1-updated-v6");
        Assert.Contains("Slide one, v6.", Assert.Single(h.S.Sent, s => s.EventId == "slide-1-part-1").Content);
        Assert.Single(h.Offs);
    }

    [Fact]
    public async Task Refused_follow_up_while_muted_keeps_the_answer_and_continue_ends_it_once()
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        await h.Answer();
        Assert.True(await h.Presenter.MuteAsync());
        await h.Settle();
        var frames = h.AskStates.Count;

        Assert.False(await h.Presenter.AskStartAsync());
        Assert.True(await h.Presenter.UnmuteAsync());
        Assert.True(await h.Presenter.ResumeAsync());
        await h.Settle();

        Assert.Equal(["off:refused_muted", "answering:sent", "off:continued"],
            h.AskStates.Skip(frames).Select(a => $"{a.State}:{a.Reason}"));
        Assert.Single(h.Offs, o => o.Reason == "continued");
        Assert.Single(h.S.Sent, IsResumeAfterQuestion);
    }

    // ---- Review r2: nested follow-ups carry the exchange's state (#4) -------------------------------------------

    [Theory]
    [InlineData("continue")]
    [InlineData("end")]
    [InlineData("max")]
    public async Task Nested_follow_ups_carry_notices_and_replay_to_the_one_exchange_end(string outcome)
    {
        await using var h = new Harness(settings: Settings(maxMinutes: 5));
        await h.StartNarrating();
        await h.TrainerOn();
        await h.ToAwaitingAnswer(start: false);
        await h.Answer();
        var id = await h.TrainOn(0);
        await h.Apply(id, 6, (0, "Slide one, v6."));
        var before = h.S.Sent.Count;

        await h.AskStart();
        await h.MicSpeech();
        await h.AskDone();
        // A real answer cannot arrive before the burst is ingested (T8: at least 1.27 s).
        await h.Advance(TimeSpan.FromMilliseconds(Presenter.ResidualStartMs));
        await h.Answer();
        await h.AskStart();
        await h.MicSpeech();
        Assert.Empty(h.Offs);
        Assert.DoesNotContain(h.S.Sent.Skip(before), s => s.Content == PromptBuilder.ScriptEditPendingInstruction() ||
            s.EventId == "slide-1-updated-v6" || s.EventId == "slide-1-part-1" || IsResumeAfterQuestion(s));

        switch (outcome)
        {
            case "end":
                Assert.True(await h.Presenter.EndAsync());
                await h.Settle();
                break;
            case "max":
                await h.Listen(TimeSpan.FromMinutes(5));
                Assert.Equal(EndReasons.MaxLength, Assert.Single(h.Closed).EndReason);
                break;
            default:
                await h.AskDone();
                // A real answer cannot arrive before the burst is ingested (T8: at least 1.27 s).
                await h.Advance(TimeSpan.FromMilliseconds(Presenter.ResidualStartMs));
                await h.Answer();
                await h.Advance(TimeSpan.FromMilliseconds(701));
                Assert.DoesNotContain(h.S.Sent.Skip(before), s => s.EventId == "slide-1-updated-v6");
                await h.Advance(TimeSpan.FromMilliseconds(Presenter.DefaultFollowUpWaitMs));
                break;
        }

        var sent = h.S.Sent.Skip(before).ToList();
        if (outcome == "continue")
        {
            Assert.Equal("continued", Assert.Single(h.Offs).Reason);
            Assert.Single(sent, s => s.Content == PromptBuilder.ScriptEditPendingInstruction());
            Assert.Single(sent, s => s.EventId == "slide-1-updated-v6");
            Assert.Contains("Slide one, v6.", Assert.Single(sent, s => s.EventId == "slide-1-part-1").Content);
            Assert.DoesNotContain(sent, IsResumeAfterQuestion);
            return;
        }

        Assert.Equal("ended", Assert.Single(h.Offs).Reason);
        Assert.DoesNotContain(sent, s => s.Content == PromptBuilder.ScriptEditPendingInstruction() ||
            s.EventId == "slide-1-updated-v6" || s.EventId == "slide-1-part-1" || IsResumeAfterQuestion(s));
        await h.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(sent.Count, h.S.Sent.Skip(before).Count());
    }

    // ---- T8 live-run defect: the resume waits for resumed narration ---------------------------------------------

    /// <summary>Brings the talk to a resume-after-question instruction on slide 1 by the given path.</summary>
    private static async Task ResumeBy(Harness h, string path)
    {
        if (path == "barge-in")
        {
            await h.StartNarrating();
            h.S.Hear("a question", 0, 100);
            await h.Settle();
            await h.Answer();
            await h.Advance(TimeSpan.FromMilliseconds(701));
            await h.Advance(TimeSpan.FromMilliseconds(Presenter.DefaultFollowUpWaitMs));
        }
        else
        {
            await h.ToCheckIn();
            switch (path)
            {
                case "yes": await h.Reply("yes"); break;
                case "continue": Assert.True(await h.Presenter.ResumeAsync()); await h.Settle(); break;
                default: await h.Advance(TimeSpan.FromMilliseconds(Presenter.DefaultFollowUpWaitMs)); break;
            }

            Assert.Equal("continued", Assert.Single(h.Offs).Reason);
        }

        Assert.Single(h.S.Sent, IsResumeAfterQuestion);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("timeout")]
    [InlineData("continue")]
    [InlineData("barge-in")]
    public async Task Resume_waits_for_the_resumed_narration_before_the_advance(string path)
    {
        await using var h = new Harness();
        await ResumeBy(h, path);

        // The live model takes seconds to speak after the instruction; the old code advanced 3 s after it.
        await h.Advance(TimeSpan.FromMilliseconds(3500));
        Assert.Equal(0, h.Presenter.Snapshot().SlideIndex);
        Assert.DoesNotContain(h.S.Sent, s => s.EventId == "slide-2-part-1");

        await h.Answer();
        await h.Advance(TimeSpan.FromMilliseconds(2999));
        Assert.Equal(0, h.Presenter.Snapshot().SlideIndex);
        await h.Advance(TimeSpan.FromMilliseconds(2));
        Assert.Equal(1, h.Presenter.Snapshot().SlideIndex);
        Assert.Single(h.S.Sent, s => s.EventId == "slide-2-part-1");
    }

    [Fact]
    public async Task Resume_with_a_silent_model_arms_the_advance_after_the_fallback()
    {
        await using var h = new Harness();
        await ResumeBy(h, "timeout");

        await h.Advance(TimeSpan.FromMilliseconds(Presenter.NudgeMs - 1));
        Assert.Equal(0, h.Presenter.Snapshot().SlideIndex);
        await h.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Contains($"resume: no audio {Presenter.NudgeMs} ms after the resume instruction; arming the advance", h.Logs);
        await h.Advance(TimeSpan.FromMilliseconds(3001));
        Assert.Equal(1, h.Presenter.Snapshot().SlideIndex);
    }

    [Fact]
    public async Task Resumed_narration_with_parts_pending_keeps_the_part_gap()
    {
        await using var h = new Harness(slides: [new(0, 1, "Long", string.Join(" ", Enumerable.Repeat("Sentence of narration.", 40)), null), BaseSlides[1]], chunkChars: 300);
        await ResumeBy(h, "continue");

        await h.Advance(TimeSpan.FromMilliseconds(3500));
        Assert.DoesNotContain(h.S.Sent, s => s.EventId == "slide-1-part-2");
        await h.Answer();
        await h.Advance(TimeSpan.FromMilliseconds(Presenter.PartGapFor(3000) + 1));
        Assert.Contains(h.S.Sent, s => s.EventId == "slide-1-part-2");
        Assert.Equal(0, h.Presenter.Snapshot().SlideIndex);
    }

    // ---- T8 live-run defect: filler before a delegation is not the answer ---------------------------------------

    [Theory]
    [InlineData("exchange")]
    [InlineData("barge-in")]
    public async Task Delegation_after_filler_speech_waits_for_the_real_answer_before_the_check_in(string kind)
    {
        await using var h = new Harness();
        if (kind == "exchange")
        {
            await h.ToAwaitingAnswer();
        }
        else
        {
            await h.StartNarrating();
            h.S.Hear("a question", 0, 100);
            await h.Settle();
        }

        // "One moment." arrives before the delegation event and is taken as the answer; the check-in begins.
        await h.Answer();
        Assert.Contains(h.Logs, l => l.StartsWith("question: answered after", StringComparison.Ordinal));
        await h.Advance(TimeSpan.FromMilliseconds(701));
        await h.Advance(TimeSpan.FromMilliseconds(2000));
        var checkIns = h.Logs.Count(l => l == "ask: check-in");

        h.S.RaiseDelegation("responses", "d1");
        await h.Settle();
        Assert.Contains("question: delegated after speech; that speech was filler, waiting for the answer", h.Logs);
        await h.Advance(TimeSpan.FromMilliseconds(Presenter.DefaultFollowUpWaitMs + 701));
        Assert.Empty(h.Offs);
        Assert.DoesNotContain(h.S.Sent, IsResumeAfterQuestion);

        h.S.RaiseDelegatedResponse("d1");
        await h.Settle();
        await h.Answer();
        Assert.DoesNotContain(h.S.Sent, IsResumeAfterQuestion);
        await h.Advance(TimeSpan.FromMilliseconds(701));
        if (kind == "exchange") Assert.Equal(checkIns + 1, h.Logs.Count(l => l == "ask: check-in"));
        await h.Advance(TimeSpan.FromMilliseconds(Presenter.DefaultFollowUpWaitMs));

        Assert.Single(h.S.Sent, IsResumeAfterQuestion);
        if (kind == "exchange") Assert.Equal("continued", Assert.Single(h.Offs).Reason);
        else Assert.Empty(h.Offs);
    }

    // ---- T8 live-run defect: the resume after an Ask names the slide and ends the pause ------------------------

    [Theory]
    [InlineData("yes")]
    [InlineData("timeout")]
    [InlineData("continue")]
    [InlineData("follow-up")]
    public async Task Resume_after_an_ask_names_the_slide_and_ends_the_pause(string exit)
    {
        await using var h = new Harness();
        if (exit == "follow-up")
        {
            await h.ToAwaitingAnswer();
            await h.Answer();
            await h.AskStart();
            await h.MicSpeech();
            await h.AskDone();
            // A real answer cannot arrive before the burst is ingested (T8: at least 1.27 s).
            await h.Advance(TimeSpan.FromMilliseconds(Presenter.ResidualStartMs));
            await h.Answer();
            await h.Advance(TimeSpan.FromMilliseconds(701));
            await h.Advance(TimeSpan.FromMilliseconds(Presenter.DefaultFollowUpWaitMs));
        }
        else
        {
            await ResumeBy(h, exit);
        }

        var resume = Assert.Single(h.S.Sent, IsResumeAfterQuestion).Content!;
        Assert.Contains("slide 1 of 3 (\"One\")", resume);
        Assert.StartsWith("The pause is over. Resume slide 1 of 3", resume);
        Assert.Contains(" now:", resume);
        Assert.Contains("natural transition of your own", resume);
        Assert.Contains(exit == "follow-up" ? "before the first question" : "when the question came", resume);
        Assert.NotEqual(PromptBuilder.ResumeAfterQuestionInstruction(), resume);
        // The ask's own pause came first; the resume is the explicit end of it.
        Assert.True(h.S.Sent.FindLastIndex(s => s.EventId == "pause-1") < h.S.Sent.FindIndex(IsResumeAfterQuestion));
    }

    [Fact]
    public async Task Barge_in_resume_without_a_pause_keeps_the_resume_after_question_wording()
    {
        await using var h = new Harness();
        await ResumeBy(h, "barge-in");

        Assert.Equal(PromptBuilder.ResumeAfterQuestionInstruction(), Assert.Single(h.S.Sent, IsResumeAfterQuestion).Content);
        Assert.DoesNotContain(h.S.Sent, s => s.EventId?.StartsWith("pause-", StringComparison.Ordinal) == true);
    }

    // ---- T8 live-run anomaly: a check-in "Yes" around a voiced model blip ---------------------------------------

    [Theory]
    [InlineData("blip-then-yes")]
    [InlineData("yes-then-blip")]
    public async Task Check_in_yes_counts_when_model_audio_re_enters_answering_around_it(string order)
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        // Answer audio begins while the question's own transcript is still arriving (live frames 643.5-644.8 s).
        h.S.Hear("delivery", 66000, 66200);
        await h.Settle();
        await h.Answer();
        h.S.Hear("And which two products are in active development", 69200, 69400);
        await h.Settle();
        await h.Answer();
        await h.Advance(TimeSpan.FromMilliseconds(701));
        Assert.Single(h.Logs, l => l == "ask: check-in");
        await h.Advance(TimeSpan.FromMilliseconds(2700));

        // The upstream hears the user's audio first, so the model may voice something (no transcript) around the
        // "Yes" delta: before it (Answering re-entered) or after it (the reply is already open).
        if (order == "blip-then-yes")
        {
            await h.Answer(200);
            h.S.Hear(" Yes", 75800, 76000);
            await h.Settle();
        }
        else
        {
            h.S.Hear(" Yes", 75800, 76000);
            await h.Settle();
            await h.Answer(200);
        }

        await h.Advance(TimeSpan.FromMilliseconds(701));

        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
        Assert.Contains("question: confirmed; resuming", h.Logs);
        Assert.Single(h.S.Sent, IsResumeAfterQuestion);
        await h.Advance(TimeSpan.FromMilliseconds(Presenter.DefaultFollowUpWaitMs + 701));
        Assert.Single(h.Logs, l => l == "ask: check-in");
        Assert.Single(h.Offs);
    }

    [Fact]
    public async Task A_voiced_blip_during_check_in_keeps_the_same_check_in_and_its_timeout()
    {
        await using var h = new Harness();
        await h.ToCheckIn();
        await h.Advance(TimeSpan.FromMilliseconds(2000));

        await h.Answer(200);
        await h.Advance(TimeSpan.FromMilliseconds(701));

        Assert.Single(h.Logs, l => l == "ask: check-in");
        Assert.Empty(h.Offs);
        await h.Advance(TimeSpan.FromMilliseconds(Presenter.DefaultFollowUpWaitMs + 1));
        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
        Assert.Single(h.Logs, l => l == "ask: check-in");
    }

    [Fact]
    public async Task A_follow_up_answer_wait_starts_before_its_check_in_again()
    {
        await using var h = new Harness();
        await h.ToCheckIn();
        await h.Reply("next slide");
        Assert.Contains("ask: unclear check-in reply; follow-up", h.Logs);
        await h.Advance(TimeSpan.FromMilliseconds(1600));

        // The follow-up's answer is not yet at its check-in: a user delta over it stays UI-only.
        await h.Answer();
        h.S.Hear("yes", 0, 100);
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(701));

        Assert.Empty(h.Offs);
        Assert.Equal(2, h.Logs.Count(l => l == "ask: check-in"));
    }

    // ---- Owner decision "hold for speech" (2026-09-24): check-in window vs. transcript lag ----------------------

    [Fact]
    public async Task Silent_check_in_times_out_five_seconds_after_the_check_in_audio_finishes_playing()
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        // 3 s of answer audio arrive at once (faster than real time): it plays until t0 + 3000.
        await h.Answer(3000);
        await h.Advance(TimeSpan.FromMilliseconds(701));
        Assert.Contains("ask: check-in", h.Logs);
        Assert.Contains(h.Logs, l => l.StartsWith("ask: check-in window starts after playback", StringComparison.Ordinal));

        await h.Advance(TimeSpan.FromMilliseconds(3000 + Presenter.DefaultFollowUpWaitMs - 701 - 2));
        Assert.Empty(h.Offs);
        await h.Advance(TimeSpan.FromMilliseconds(3));
        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
    }

    [Fact]
    public async Task Check_in_window_follows_the_chained_playback_of_every_answer_chunk()
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        // Review r4 #B2: 3 s of audio at t0, then 2 s more at t0 + 500 while the first still plays. Playback is
        // chained: it ends at t0 + 5000 (not t0 + 3000 from the first chunk, nor t0 + 2500 from the last one).
        await h.Answer(3000);
        await h.Advance(TimeSpan.FromMilliseconds(500));
        await h.Answer(2000);
        await h.Advance(TimeSpan.FromMilliseconds(701));
        Assert.Contains("ask: check-in", h.Logs);

        await h.Advance(TimeSpan.FromMilliseconds(5000 + Presenter.DefaultFollowUpWaitMs - 500 - 701 - 2));
        Assert.Empty(h.Offs);
        await h.Advance(TimeSpan.FromMilliseconds(3));
        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
    }

    [Fact]
    public async Task Silent_check_in_after_a_short_answer_keeps_the_existing_window()
    {
        await using var h = new Harness();
        await h.ToCheckIn();

        await h.Advance(TimeSpan.FromMilliseconds(Presenter.DefaultFollowUpWaitMs - 3));
        Assert.Empty(h.Offs);
        await h.Advance(TimeSpan.FromMilliseconds(3));
        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
        Assert.DoesNotContain("ask: check-in held for listener speech", h.Logs);
    }

    [Theory]
    [InlineData("before-window", 3000, 1200)]
    [InlineData("late-in-window", 300, 3000)]
    public async Task Check_in_reply_whose_transcript_lags_the_mic_speech_by_2_7_s_counts(
        string when, int answerMs, int speechAfterCheckInMs)
    {
        _ = when;
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        await h.Answer(answerMs);
        await h.Advance(TimeSpan.FromMilliseconds(701));
        await h.Advance(TimeSpan.FromMilliseconds(speechAfterCheckInMs));

        // The listener says "No" for about a second; its transcript arrives 2.7 s after the speech ends.
        await MicSpeechLive(h, 1000);
        await h.Advance(TimeSpan.FromMilliseconds(2700));
        Assert.Empty(h.Offs);
        h.S.Hear("No.", 0, 100);
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(701));

        Assert.Equal("waiting", Assert.Single(h.Offs).Reason);
        Assert.DoesNotContain(h.S.Sent, IsResumeAfterQuestion);
    }

    [Fact]
    public async Task Speech_hold_never_makes_a_stale_delta_count()
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        await h.Answer();
        h.S.Hear("and the budget", 0, 100);
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(701));
        Assert.Contains("ask: check-in", h.Logs);

        // Within 1.5 s of the previous user delta: UI-only (P-13), and the speech hold below must not change that.
        await h.Advance(TimeSpan.FromMilliseconds(300));
        h.S.Hear("no", 0, 100);
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(3700));
        await MicSpeechLive(h, 1000);
        await h.Advance(TimeSpan.FromMilliseconds(CheckInGraceMs + 1));

        Assert.Contains("ask: check-in held for listener speech", h.Logs);
        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
    }

    [Fact]
    public async Task Speech_hold_is_bounded_while_the_mic_keeps_hearing_voice()
    {
        await using var h = new Harness();
        await h.ToCheckIn();

        // Continuous voice (a noisy room): the window closes at its normal end plus CheckInMaxHoldMs.
        for (var i = 0; i < 29; i++)
        {
            await h.MicSpeech(100);
            await h.Advance(TimeSpan.FromMilliseconds(500));
        }

        Assert.Empty(h.Offs);
        for (var i = 0; i < 3; i++)
        {
            await h.MicSpeech(100);
            await h.Advance(TimeSpan.FromMilliseconds(500));
        }

        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
    }

    [Fact]
    public async Task Late_check_in_transcript_after_the_timeout_does_not_open_a_question_hold()
    {
        await using var h = new Harness();
        await h.ToCheckIn();
        var holds = h.Logs.Count(l => l == "question: hold opened");
        await h.Advance(TimeSpan.FromMilliseconds(Presenter.DefaultFollowUpWaitMs + 1));
        Assert.Equal("continued", Assert.Single(h.Offs).Reason);

        // The reply was not heard by the mic hold (e.g. too quiet); its transcript lands 2.7 s after the timeout.
        await h.Advance(TimeSpan.FromMilliseconds(2700));
        h.S.Hear("No.", 0, 100);
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(701));

        Assert.Contains("ask: late check-in reply after the exchange ended; ignored", h.Logs);
        Assert.Equal(holds, h.Logs.Count(l => l == "question: hold opened"));
        Assert.Single(h.S.Sent, IsResumeAfterQuestion);

        // The guard is bounded: a new question later opens the hold as before.
        await h.Advance(TimeSpan.FromSeconds(5));
        h.S.Hear("What about the budget?", 0, 100);
        await h.Settle();
        Assert.Equal(holds + 1, h.Logs.Count(l => l == "question: hold opened"));
    }

    // ---- T8 regression: residual narration after Ask done, cut-off nudge at the cap (2026-09-24) -------------------

    /// <summary>Talk → Ask → speech; the narration begun before Ask keeps streaming while muted up to the send.</summary>
    private static async Task ToSendWithResidual(Harness h)
    {
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSpeech();
        await h.Advance(TimeSpan.FromSeconds(3));
        h.S.Speak(200);
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(300));
        await h.AskDone();
    }

    private const string ResidualOpened = "ask: residual model audio at send; held until a gap";

    private static bool Answered(Harness h) =>
        h.Logs.Any(l => l.StartsWith("question: answered after", StringComparison.Ordinal));

    [Fact]
    public async Task Rest_of_the_narration_held_while_muted_and_released_after_the_send_is_not_the_answer()
    {
        // T8 row 4: narration cut by Ask, nothing voiced while listening, the interrupted word arrived 205 ms after the
        // send and was taken as the answer; the real answer began at +1.35 s.
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSpeech();
        await h.Advance(TimeSpan.FromMilliseconds(4_400));
        await h.AskDone();
        var forwarded = h.Forwarded;

        await h.Advance(TimeSpan.FromMilliseconds(205));
        h.S.Speak(200);
        await h.Settle();
        Assert.Contains(ResidualOpened, h.Logs);
        Assert.Equal(forwarded, h.Forwarded);
        Assert.False(Answered(h));
        await h.Advance(TimeSpan.FromMilliseconds(1_350 - 205));
        Assert.DoesNotContain("ask: check-in", h.Logs);

        await h.Answer();
        Assert.Contains("ask: residual model audio ended; dropped 200 ms", h.Logs);
        Assert.True(h.Forwarded > forwarded);
        Assert.True(Answered(h));
        await h.Advance(TimeSpan.FromMilliseconds(701));
        Assert.Contains("ask: check-in", h.Logs);
        Assert.Empty(h.Offs);
    }

    [Fact]
    public async Task Residual_narration_after_the_send_is_held_and_the_audio_after_a_gap_is_the_answer()
    {
        // T8 live run: the rest of the narration stream was taken as the answer 64 ms after Ask done.
        await using var h = new Harness();
        await ToSendWithResidual(h);
        var forwarded = h.Forwarded;

        for (var i = 0; i < 3; i++)
        {
            h.S.Speak(200);
            h.S.Silence(100);
            await h.Settle();
            await h.Advance(TimeSpan.FromMilliseconds(300));
        }

        await h.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains(ResidualOpened, h.Logs);
        Assert.Equal(forwarded, h.Forwarded);
        Assert.False(Answered(h));
        Assert.DoesNotContain("ask: check-in", h.Logs);

        await h.Answer();
        Assert.Contains("ask: residual model audio ended; dropped 900 ms", h.Logs);
        Assert.True(h.Forwarded > forwarded);
        Assert.True(Answered(h));
        await h.Advance(TimeSpan.FromMilliseconds(701));
        Assert.Contains("ask: check-in", h.Logs);
        Assert.Empty(h.Offs);
    }

    [Theory]
    [InlineData(Presenter.ResidualStartMs - 1, true)]
    [InlineData(Presenter.ResidualStartMs, false)]
    [InlineData(Presenter.ResidualStartMs + 500, false)]
    public async Task Candidate_first_voiced_frame_before_the_start_window_is_residual_and_from_it_the_answer(int afterSendMs,
        bool residual)
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSpeech();
        await h.AskDone();
        var forwarded = h.Forwarded;

        await h.Advance(TimeSpan.FromMilliseconds(afterSendMs));
        await h.Answer();

        Assert.Equal(residual, h.Logs.Contains(ResidualOpened));
        Assert.Equal(residual, h.Forwarded == forwarded);
        Assert.Equal(!residual, Answered(h));
    }

    [Fact]
    public async Task Model_silent_before_ask_start_and_while_listening_makes_early_audio_the_answer()
    {
        await using var h = new Harness();
        await h.StartNarrating();
        await h.Advance(TimeSpan.FromMilliseconds(Presenter.ResidualGapMs + 1));
        await h.AskStart();
        await h.MicSpeech();
        await h.Advance(TimeSpan.FromSeconds(2));
        await h.AskDone();
        var forwarded = h.Forwarded;

        await h.Advance(TimeSpan.FromMilliseconds(100));
        await h.Answer();

        Assert.DoesNotContain(ResidualOpened, h.Logs);
        Assert.True(h.Forwarded > forwarded);
        Assert.True(Answered(h));
    }

    [Fact]
    public async Task Residual_still_streaming_at_the_budget_extends_the_wait_up_to_the_ceiling_then_resumes()
    {
        await using var h = new Harness();
        await ToSendWithResidual(h);
        const int stepMs = 500;
        for (var at = 0; at < Presenter.AnswerStartBudgetMs + Presenter.AnswerCeilingExtraMs - stepMs; at += stepMs)
        {
            h.S.Speak(100);
            await h.Settle();
            await h.Advance(TimeSpan.FromMilliseconds(stepMs));
            Assert.Empty(h.Offs);
        }

        Assert.Contains("ask: residual model audio still arriving; waiting longer for the answer", h.Logs);
        h.S.Speak(100);
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(stepMs));

        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
        Assert.Contains("ask: no answer within 75 s", h.Logs);
        Assert.False(Answered(h));
    }

    [Theory]
    [InlineData("navigate")]
    [InlineData("follow_up")]
    public async Task Residual_state_does_not_carry_into_the_next_ask(string how)
    {
        await using var h = new Harness();
        await ToSendWithResidual(h);
        var forwarded = h.Forwarded;
        h.S.Speak(200);
        await h.Settle();
        Assert.Equal(forwarded, h.Forwarded);
        Assert.Contains(ResidualOpened, h.Logs);

        if (how == "navigate")
        {
            Assert.True(await h.Presenter.NextAsync());
            await h.Settle();
            Assert.Equal("navigated", Assert.Single(h.Offs).Reason);
            h.S.Speak(100);
            await h.Settle();
            await h.Advance(TimeSpan.FromSeconds(1));
            await h.AskStart();
            await h.MicSpeech();
            await h.Advance(TimeSpan.FromSeconds(1));
            await h.AskDone();
            forwarded = h.Forwarded;
            await h.Answer();

            // Not a candidate: the next answer is heard at once and closes no stale residual.
            Assert.Single(h.Logs, l => l == ResidualOpened);
            Assert.DoesNotContain(h.Logs, l => l.StartsWith("ask: residual model audio ended", StringComparison.Ordinal));
        }
        else
        {
            // The follow-up is a candidate of its own: its residual is decided and counted afresh.
            await h.AskStart();
            await h.MicSpeech();
            await h.Advance(TimeSpan.FromSeconds(1));
            await h.AskDone();
            h.S.Speak(200);
            await h.Settle();
            Assert.Equal(2, h.Logs.Count(l => l == ResidualOpened));
            await h.Advance(TimeSpan.FromSeconds(1));
            forwarded = h.Forwarded;
            await h.Answer();

            Assert.Single(h.Logs, l => l.StartsWith("ask: residual model audio ended", StringComparison.Ordinal));
            Assert.Contains("ask: residual model audio ended; dropped 200 ms", h.Logs);
        }

        Assert.True(h.Forwarded > forwarded);
        Assert.True(Answered(h));
    }

    /// <summary>Talk → Ask → speech past the 25 s cap: the burst goes out with <c>limit_sent</c>.</summary>
    private static async Task ToCapSent(Harness h)
    {
        await h.StartNarrating();
        await h.AskStart();
        await h.MicSpeech(AskRecorder.MaxRetainedMs + 1_000);
        Assert.Equal("limit_sent", h.AskStates.Single(s => s.State == "answering").Reason);
    }

    private static int CutOffs(Harness h) => h.S.Sent.Count(s => s.Content == PromptBuilder.AskCutOffInstruction());

    [Fact]
    public async Task Question_cut_off_at_the_cap_without_an_answer_gets_one_nudge()
    {
        // T8 live run: the burst's transcript ended mid-sentence and the model waited 15 s for the rest (3/3 runs).
        await using var h = new Harness();
        await ToCapSent(h);

        await h.Advance(TimeSpan.FromMilliseconds(Presenter.CutOffNudgeMs - 1));
        Assert.Equal(0, CutOffs(h));
        await h.Advance(TimeSpan.FromMilliseconds(2));

        Assert.Equal(1, CutOffs(h));
        Assert.Contains("ask: question cut off at the cap; asked the model to answer what it heard", h.Logs);
        h.S.Hear("what did", 0, 100);
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(Presenter.AnswerStartBudgetMs));
        Assert.Equal(1, CutOffs(h));
    }

    [Fact]
    public async Task Burst_transcript_deltas_postpone_the_cut_off_nudge()
    {
        await using var h = new Harness();
        await ToCapSent(h);
        await h.Advance(TimeSpan.FromMilliseconds(2_500));
        h.S.Hear("What did the program", 0, 100);
        await h.Settle();

        await h.Advance(TimeSpan.FromMilliseconds(Presenter.CutOffQuietMs - 1));
        Assert.Equal(0, CutOffs(h));
        await h.Advance(TimeSpan.FromMilliseconds(2));
        Assert.Equal(1, CutOffs(h));
    }

    [Fact]
    public async Task A_capped_question_waits_its_kept_speech_longer_for_the_answer()
    {
        // T8 re-run: the upstream took a cut-off 25 s burst in at about its own length; the reply came at +24.6 s, after
        // the 15 s budget (re-armed from the nudge at +10.4 s) had resumed the talk over it.
        await using var h = new Harness();
        await ToCapSent(h);
        var budget = Presenter.AnswerStartBudgetMs + AskRecorder.MaxRetainedMs;

        await h.Advance(TimeSpan.FromMilliseconds(Presenter.AnswerStartBudgetMs + 1));
        Assert.Equal(1, CutOffs(h));
        Assert.DoesNotContain(h.Logs, l => l.StartsWith("ask: no answer within", StringComparison.Ordinal));
        await h.Advance(TimeSpan.FromMilliseconds(budget - Presenter.AnswerStartBudgetMs - 2));
        Assert.DoesNotContain(h.Logs, l => l.StartsWith("ask: no answer within", StringComparison.Ordinal));
        await h.Advance(TimeSpan.FromMilliseconds(2));
        Assert.Contains("ask: no answer within 40 s", h.Logs);
    }

    [Fact]
    public async Task A_late_cut_off_nudge_extends_the_answer_budget_and_never_shortens_it()
    {
        await using var h = new Harness();
        await ToCapSent(h);
        // The burst's transcript keeps streaming for 30 s: the nudge comes 2 s after its last delta (+32 s).
        for (var i = 0; i < 20; i++)
        {
            await h.Advance(TimeSpan.FromMilliseconds(1_500));
            h.S.Hear("more of the question", 0, 100);
            await h.Settle();
        }

        await h.Advance(TimeSpan.FromMilliseconds(Presenter.CutOffQuietMs));
        Assert.Equal(1, CutOffs(h));

        // The capped budget (40 s from the send) would end at +40 s; the nudge gives at least 15 s from +32 s.
        await h.Advance(TimeSpan.FromMilliseconds(Presenter.AnswerStartBudgetMs - 1));
        Assert.DoesNotContain(h.Logs, l => l.StartsWith("ask: no answer within", StringComparison.Ordinal));
        await h.Advance(TimeSpan.FromMilliseconds(2));
        Assert.Contains("ask: no answer within 47 s", h.Logs);
    }

    [Fact]
    public async Task Answer_before_the_cut_off_nudge_cancels_it()
    {
        await using var h = new Harness();
        await ToCapSent(h);
        await h.Advance(TimeSpan.FromMilliseconds(1_000));

        await h.Answer();
        await h.Advance(TimeSpan.FromMilliseconds(Presenter.CutOffNudgeMs + Presenter.CutOffQuietMs));

        Assert.Equal(0, CutOffs(h));
    }

    [Fact]
    public async Task Question_sent_by_ask_done_never_gets_the_cut_off_nudge()
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        Assert.Equal("sent", h.AskStates[^1].Reason);

        await h.Advance(TimeSpan.FromMilliseconds(Presenter.AnswerStartBudgetMs + 1));

        Assert.Equal(0, CutOffs(h));
        Assert.Equal("continued", Assert.Single(h.Offs).Reason);
    }

    private static int AnswerNows(Harness h) => h.S.Sent.Count(s => s.Content == PromptBuilder.AskAnswerNowInstruction());

    [Fact]
    public async Task Complete_question_without_an_answer_gets_one_answer_now_nudge_and_keeps_its_budget()
    {
        // T8 regression run: after a busy-refused tool round the model left this and the next ask unanswered for 15 s
        // each, while it still obeyed the resume instructions.
        await using var h = new Harness(registry: Registry(new GateTool("slow", new TaskCompletionSource<ToolResult>().Task)));
        await h.StartNarrating();
        await h.AskStart();
        h.S.RaiseToolCall("d1", "c1", "slow", "{}");
        await h.Settle();
        await h.MicSpeech();
        await h.AskDone();

        await h.Advance(TimeSpan.FromMilliseconds(Presenter.AnswerNudgeMs - 1));
        Assert.Equal(0, AnswerNows(h));
        await h.Advance(TimeSpan.FromMilliseconds(2));
        Assert.Equal(1, AnswerNows(h));
        Assert.EndsWith("-answer-now", h.S.Sent.Single(s => s.Content == PromptBuilder.AskAnswerNowInstruction()).EventId);
        Assert.Contains("ask: no answer yet; asked the model to answer the question now", h.Logs);
        Assert.Equal(0, CutOffs(h));

        // The budget still ends 15 s after the send, and the nudge is never repeated.
        await h.Advance(TimeSpan.FromMilliseconds(Presenter.AnswerStartBudgetMs - Presenter.AnswerNudgeMs - 2));
        Assert.DoesNotContain(h.Logs, l => l.StartsWith("ask: no answer within", StringComparison.Ordinal));
        await h.Advance(TimeSpan.FromMilliseconds(2));
        Assert.Contains("ask: no answer within 15 s", h.Logs);
        Assert.Equal(1, AnswerNows(h));
    }

    [Fact]
    public async Task Question_transcript_deltas_postpone_the_answer_now_nudge()
    {
        // A long complete question is transcribed for seconds after the send (answered at +8 s for 19 s kept, T8).
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        await h.Advance(TimeSpan.FromMilliseconds(Presenter.AnswerNudgeMs - Presenter.ResidualStartMs - 500));
        h.S.Hear("and which two products", 0, 100);
        await h.Settle();

        await h.Advance(TimeSpan.FromMilliseconds(Presenter.AnswerNudgeQuietMs - 1));
        Assert.Equal(0, AnswerNows(h));
        await h.Advance(TimeSpan.FromMilliseconds(2));
        Assert.Equal(1, AnswerNows(h));
    }

    [Theory]
    [InlineData("answer")]
    [InlineData("delegation")]
    public async Task Answer_or_answer_work_before_the_answer_now_nudge_cancels_it(string started)
    {
        await using var h = new Harness();
        await h.ToAwaitingAnswer();
        if (started == "answer") await h.Answer();
        else
        {
            h.S.RaiseDelegation("responses", "d1");
            await h.Settle();
        }

        await h.Advance(TimeSpan.FromMilliseconds(Presenter.AnswerNudgeMs + Presenter.AnswerNudgeQuietMs));

        Assert.Equal(0, AnswerNows(h));
    }

    private const int CheckInGraceMs = Presenter.CheckInTranscriptGraceMs;

    /// <summary>Voiced mic audio in real time: 100 ms frames with the clock advancing between them.</summary>
    private static async Task MicSpeechLive(Harness h, int milliseconds)
    {
        for (var i = 0; i < milliseconds / 100; i++)
        {
            await h.MicSpeech(100);
            await h.Advance(TimeSpan.FromMilliseconds(100));
        }
    }

    // ---- Harness --------------------------------------------------------------------------------------------------

    private static bool IsResumeAfterQuestion((string Type, string? Content, string? EventId, string? DelegationId) item) =>
        item.EventId?.Contains("-resume-", StringComparison.Ordinal) == true;

    private static PresenterSettings Settings(int maxMinutes = 60, int idleSeconds = 300) =>
        new(3000, "marin", Presenter.DefaultFollowUpWaitMs, 16, maxMinutes, 120, 120, idleSeconds);

    private static ToolRegistry Registry(params ITool[] tools)
    {
        var registry = new ToolRegistry();
        foreach (var tool in tools) registry.Register(tool);
        return registry;
    }

    /// <summary>A 20 ms PCM16 frame whose samples alternate ±<paramref name="level"/> (RMS exactly the level).</summary>
    private static byte[] Pcm(int level)
    {
        var frame = new byte[AskRecorder.WindowBytes];
        for (var i = 0; i < frame.Length / 2; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(i * 2), (short)(i % 2 == 0 ? level : -level));
        }

        return frame;
    }

    private static async Task Eventually(Func<bool> predicate)
    {
        for (var i = 0; i < 500; i++)
        {
            try
            {
                if (predicate()) return;
            }
            catch (InvalidOperationException)
            {
            }

            await Task.Delay(10);
        }

        Assert.True(predicate());
    }

    private sealed class GateTool(string name, Task<ToolResult> gate, bool confirmation = false) : ITool
    {
        public string Name => name;
        public string Description => "A tool that finishes when the test says so.";
        public JsonObject Parameters { get; } = new() { ["type"] = "object", ["properties"] = new JsonObject() };
        public IReadOnlyList<string> Tags => [];
        public bool Pinned => true;
        public bool RequiresConfirmation => confirmation;
        public TimeSpan Timeout => TimeSpan.FromMinutes(5);
        public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken) => gate.WaitAsync(cancellationToken);
    }

    /// <summary>Waits for the gate inside its own invocation, then calls Next: the command carries its call id.</summary>
    private sealed class NextAfterGateTool(Task gate) : ITool
    {
        public IPresenter? Presenter { get; set; }
        public string Name => "gate_next";
        public string Description => "Moves on when the test says so.";
        public JsonObject Parameters { get; } = new() { ["type"] = "object", ["properties"] = new JsonObject() };
        public IReadOnlyList<string> Tags => [];
        public bool Pinned => true;
        public TimeSpan Timeout => TimeSpan.FromMinutes(5);

        public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            return await Presenter!.NextAsync(cancellationToken) ? ToolResult.Success("moved") : ToolResult.Failure("refused");
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(
            PresenterSettings? settings = null,
            IReadOnlyList<Slide>? slides = null,
            IAskTranscriber? transcriber = null,
            Action<FakeSession, int>? configure = null,
            ToolRegistry? registry = null,
            int chunkChars = 1400)
        {
            Configure = configure;
            Transcriber = new TestAskTranscriber();
            Presenter = new Presenter(
                (request, _) =>
                {
                    var session = new FakeSession { Request = request };
                    Configure?.Invoke(session, Sessions.Count);
                    Sessions.Add(session);
                    return session;
                },
                (_, id, _) => Task.FromResult(new LoadedPresentation(id,
                    new PresentationMeta(id, "Title", "deck", "show", null, null, 3000, chunkChars), slides ?? BaseSlides, null, 5)),
                settings ?? Settings(),
                Clock,
                registry,
                _ => true,
                null,
                null,
                Service,
                transcriber ?? Transcriber);
            Presenter.Log += log => Logs.Enqueue(log.Message);
            Presenter.AskState += state =>
            {
                AskStates.Add(state);
                Mark($"ask:{state.State}:{state.Reason}");
                OnAsk?.Invoke(state);
            };
            Presenter.Closed += closed => { Closed.Add(closed); Mark("closed"); };
            Presenter.Flush += () => { Flushes++; Mark("flush"); };
            Presenter.State += snapshot => Mark($"state:{snapshot.State}");
            Presenter.Usage += usage => { Usages.Add(usage); Mark("usage"); };
            Presenter.UpstreamStatus += status => Mark($"upstream:{status.Status}");
            Presenter.LimitWarning += Warnings.Add;
            Presenter.Audio += _ => Interlocked.Increment(ref _forwarded);
        }

        private int _forwarded;

        public FakeTimeProvider Clock { get; } = new();
        public FakeScriptRevisionService Service { get; } = new();
        public TestAskTranscriber Transcriber { get; }
        public Presenter Presenter { get; }
        public Action<FakeSession, int>? Configure { get; set; }
        public Action<PresenterAskState>? OnAsk { get; set; }
        public List<FakeSession> Sessions { get; } = [];
        public ConcurrentQueue<string> Logs { get; } = new();
        public List<PresenterAskState> AskStates { get; } = [];
        public List<PresenterClosed> Closed { get; } = [];
        public List<PresenterUsage> Usages { get; } = [];
        public List<PresenterLimitWarning> Warnings { get; } = [];
        public List<(string Kind, int SentCount)> Marks { get; } = [];
        public int Flushes { get; private set; }
        public int Forwarded => Volatile.Read(ref _forwarded);
        public FakeSession S => Sessions[^1];
        public IReadOnlyList<PresenterAskState> Offs => AskStates.Where(s => s.State == "off").ToList();

        public Task Settle() => Presenter.WaitUntilIdleAsync();

        public async Task Advance(TimeSpan by)
        {
            Clock.Advance(by);
            await Settle();
        }

        public async Task Start()
        {
            Assert.True((await Presenter.StartAsync(Pid, null, Owner)).Started);
            await Settle();
        }

        /// <summary>Starts and hears the first narration audio, so the slide has output.</summary>
        public async Task StartNarrating()
        {
            await Start();
            S.Speak(100);
            await Settle();
        }

        public async Task TrainerOn()
        {
            Assert.True(await Presenter.SetTrainerModeAsync(Owner, true));
            await Settle();
        }

        public async Task<string> TrainOn(int slideIndex)
        {
            Assert.True(await Presenter.TrainOnTurnAsync(Owner, "What about it?", "It is like this.", slideIndex));
            await Settle();
            return Service.Enqueued[^1].Id;
        }

        public async Task Apply(string editId, int version, params (int Index, string Narration)[] changes)
        {
            var targets = Service.Enqueued.Single(e => e.Id == editId).Request.TargetSlideIndexes;
            Assert.True(Service.SetOutcome(editId, EditOutcome.Applied(targets, version, "applied"), With(version, changes)));
            Service.RaiseChanged(Pid);
            await Settle();
        }

        public async Task Revert(int version, params (int Index, string Narration)[] changes)
        {
            Service.Observe(With(version, changes));
            Service.RaiseChanged(Pid);
            await Settle();
        }

        private HeadSnapshot With(int version, (int Index, string Narration)[] changes)
        {
            var slides = Service.Head(Pid)!.Slides.ToArray();
            foreach (var (index, narration) in changes) slides[index] = slides[index] with { Narration = narration };
            return new HeadSnapshot(Pid, version, slides);
        }

        public async Task AskStart()
        {
            Assert.True(await Presenter.AskStartAsync());
            await Settle();
        }

        public async Task AskDone()
        {
            Assert.True(await Presenter.AskDoneAsync());
            await Settle();
        }

        /// <summary>Voiced mic PCM in 20 ms frames (mic time, not clock time).</summary>
        public async Task MicSpeech(int milliseconds = 1000)
        {
            var frame = AudioLevelTests.VoicedFrame(AskRecorder.WindowBytes);
            for (var i = 0; i < milliseconds / AskRecorder.WindowMs; i++) await Presenter.SendAudioAsync(frame);
            await Settle();
        }

        public async Task MicSilence(int milliseconds)
        {
            var frame = new byte[AskRecorder.WindowBytes];
            for (var i = 0; i < milliseconds / AskRecorder.WindowMs; i++) await Presenter.SendAudioAsync(frame);
            await Settle();
        }

        /// <summary>Keeps listening for <paramref name="duration"/> of clock time, pressing Extend every 60 s.</summary>
        public async Task Listen(TimeSpan duration)
        {
            var left = duration;
            while (left > TimeSpan.Zero)
            {
                var step = left < TimeSpan.FromSeconds(60) ? left : TimeSpan.FromSeconds(60);
                await Advance(step);
                left -= step;
                if (AskStates.Count > 0 && AskStates[^1].State == "listening") await Presenter.AskExtendAsync();
            }

            await Settle();
        }

        /// <summary>Narrating talk → Ask → 1 s of speech → Ask done: the exchange awaits the answer.</summary>
        public async Task ToAwaitingAnswer(bool start = true)
        {
            if (start) await StartNarrating();
            await AskStart();
            await MicSpeech();
            await AskDone();
            // A real answer cannot arrive before the burst is ingested (T8: at least 1.27 s).
            await Advance(TimeSpan.FromMilliseconds(Presenter.ResidualStartMs));
            Assert.Equal("answering", AskStates[^1].State);
        }

        public async Task ToCheckIn()
        {
            await ToAwaitingAnswer();
            await Answer();
            await Advance(TimeSpan.FromMilliseconds(701));
            Assert.Contains("ask: check-in", Logs);
        }

        /// <summary>Voiced answer audio from the model.</summary>
        public async Task Answer(int milliseconds = 300)
        {
            S.Speak(milliseconds);
            await Settle();
        }

        /// <summary>A user reply, then the 700 ms utterance gap.</summary>
        public async Task Reply(string text)
        {
            S.Hear(text, 0, 100);
            await Settle();
            await Advance(TimeSpan.FromMilliseconds(701));
        }

        public ValueTask DisposeAsync() => Presenter.DisposeAsync();

        private void Mark(string kind) => Marks.Add((kind, Sessions.Count == 0 ? 0 : S.Sent.Count));
    }
}
