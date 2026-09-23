using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Presenting.Tools;
using PresenterAi.Application.Scripts;
using PresenterAi.Application.Tools;
using Xunit;

namespace PresenterAi.Application.Tests.Presenting;

public sealed class PresenterToolTests
{
    [Fact]
    public async Task Each_tool_changes_state_and_returns_right_result()
    {
        await using var harness = Create();
        Assert.True(await harness.Presenter.StartAsync("p"));
        var session = harness.Session();
        Assert.Equal("presenting", harness.Presenter.Snapshot().State);

        // 1. pause_presentation
        session.RaiseToolCall("del_1", "c_pause", "pause_presentation", "{}");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "c_pause");
        Assert.True(harness.Presenter.Snapshot().Paused);
        Assert.Equal("paused", harness.Presenter.Snapshot().State);
        var pauseOutput = ParseOutput(session, "c_pause");
        Assert.True(pauseOutput.Ok);
        Assert.StartsWith("paused on slide 1 of 3", pauseOutput.Message);

        // pause_presentation again -> already paused
        session.RaiseToolCall("del_1", "c_pause_again", "pause_presentation", "{}");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "c_pause_again");
        var pauseAgainOutput = ParseOutput(session, "c_pause_again");
        Assert.True(pauseAgainOutput.Ok);
        Assert.StartsWith("already paused", pauseAgainOutput.Message);

        // 2. resume_presentation
        session.RaiseToolCall("del_1", "c_resume", "resume_presentation", "{}");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "c_resume");
        Assert.False(harness.Presenter.Snapshot().Paused);
        Assert.Equal("presenting", harness.Presenter.Snapshot().State);
        var resumeOutput = ParseOutput(session, "c_resume");
        Assert.True(resumeOutput.Ok);
        Assert.StartsWith("resumed on slide 1 of 3", resumeOutput.Message);

        // resume_presentation again -> already presenting
        session.RaiseToolCall("del_1", "c_resume_again", "resume_presentation", "{}");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "c_resume_again");
        var resumeAgainOutput = ParseOutput(session, "c_resume_again");
        Assert.True(resumeAgainOutput.Ok);
        Assert.StartsWith("already presenting", resumeAgainOutput.Message);

        // 3. next_slide
        Assert.Equal(0, harness.Presenter.Snapshot().SlideIndex);
        session.RaiseToolCall("del_1", "c_next", "next_slide", "{}");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "c_next");
        Assert.Equal(1, harness.Presenter.Snapshot().SlideIndex);
        var nextOutput = ParseOutput(session, "c_next");
        Assert.True(nextOutput.Ok);
        Assert.Equal("moved to slide 2 of 3", nextOutput.Message);

        // 4. previous_slide
        session.RaiseToolCall("del_1", "c_prev", "previous_slide", "{}");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "c_prev");
        Assert.Equal(0, harness.Presenter.Snapshot().SlideIndex);
        var prevOutput = ParseOutput(session, "c_prev");
        Assert.True(prevOutput.Ok);
        Assert.Equal("moved to slide 1 of 3", prevOutput.Message);

        // previous_slide when already on first slide -> already on slide 1
        session.RaiseToolCall("del_1", "c_prev_again", "previous_slide", "{}");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "c_prev_again");
        var prevAgainOutput = ParseOutput(session, "c_prev_again");
        Assert.True(prevAgainOutput.Ok);
        Assert.Equal("already on slide 1 of 3", prevAgainOutput.Message);

        // 5. go_to_slide
        session.RaiseToolCall("del_1", "c_goto", "go_to_slide", "{\"slide_number\": 3}");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "c_goto");
        Assert.Equal(2, harness.Presenter.Snapshot().SlideIndex);
        var gotoOutput = ParseOutput(session, "c_goto");
        Assert.True(gotoOutput.Ok);
        Assert.Equal("moved to slide 3 of 3", gotoOutput.Message);

        // go_to_slide to current slide -> already on slide 3
        session.RaiseToolCall("del_1", "c_goto_same", "go_to_slide", "{\"slide_number\": 3}");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "c_goto_same");
        var gotoSameOutput = ParseOutput(session, "c_goto_same");
        Assert.True(gotoSameOutput.Ok);
        Assert.Equal("already on slide 3 of 3", gotoSameOutput.Message);

        // 6. end_presentation with confirmed: false -> pauses and asks confirmation, not ended
        session.RaiseToolCall("del_1", "c_end_unconfirmed", "end_presentation", "{\"confirmed\": false}");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "c_end_unconfirmed");
        Assert.True(harness.Presenter.Snapshot().Paused);
        Assert.NotEqual("ended", harness.Presenter.Snapshot().State);
        var endUnconfOutput = ParseOutput(session, "c_end_unconfirmed");
        Assert.True(endUnconfOutput.Ok);
        Assert.Contains("confirmation required", endUnconfOutput.Message);

        // 7. end_presentation with confirmed: true but no confirmation awaiting an answer -> still only asks
        // (plan 007 §3.4: the model cannot end the talk by itself)
        session.RaiseToolCall("del_1", "c_end_confirmed", "end_presentation", "{\"confirmed\": true}");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "c_end_confirmed");
        Assert.NotEqual("idle", harness.Presenter.Snapshot().State);
        Assert.DoesNotContain(session.Sent, s => s.Type == "close");
        var endConfOutput = ParseOutput(session, "c_end_confirmed");
        Assert.False(endConfOutput.Ok);
        Assert.Contains("confirmation required", endConfOutput.Message);
    }

    [Fact]
    public async Task Confirmed_end_tool_closes_only_after_question_is_ready()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();
        session.RaiseToolCall("d", "ask", "end_presentation", "{\"confirmed\":false}");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "ask");
        session.Speak(startMs: 100, endMs: 150);
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(501));
        await harness.Flush();
        session.RaiseToolCall("d", "confirm", "end_presentation", "{\"confirmed\":true}");
        // Ending clears the round: the actual close and final state, not a tool output, are the oracle.
        await harness.WaitForSentAsync(s => s.Type == "close");
        await harness.Flush();
        Assert.Equal("idle", harness.Presenter.Snapshot().State);
    }

    [Fact]
    public async Task Same_slide_goto_cancels_end_confirmation_and_resume_tool_clears_check_in()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();
        await harness.Presenter.RequestEndConfirmationAsync(false);
        session.RaiseToolCall("d", "same", "go_to_slide", "{\"slide_number\":1}");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "same");
        Assert.True(ParseOutput(session, "same").Ok);
        Assert.Equal("presenting", harness.Presenter.Snapshot().State);
        Assert.True(await harness.Presenter.RequestEndConfirmationAsync(true));
        Assert.Equal("paused", harness.Presenter.Snapshot().State);
        Assert.DoesNotContain(session.Sent, s => s.Type == "close");
        await harness.Presenter.ResumeAsync();
        session.Hear("question", 100, 150);
        await harness.Flush();
        session.Speak(startMs: 151, endMs: 200);
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(701));
        await harness.Flush();
        session.RaiseToolCall("d", "resume", "resume_presentation", "{}");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "resume");
        harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.DefaultFollowUpWaitMs + 2000));
        await harness.Flush();
        Assert.DoesNotContain(session.Sent, s => s.EventId?.Contains("-resume-") == true);
    }

    [Fact]
    public async Task Already_presenting_resume_tool_clears_check_in_without_resume_bridge()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();
        session.Hear("question", 100, 150);
        await harness.Flush();
        session.Speak(startMs: 151, endMs: 200);
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromMilliseconds(701));
        await harness.Flush();
        session.RaiseToolCall("d", "resume", "resume_presentation", "{}");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "resume");
        Assert.True(ParseOutput(session, "resume").Ok);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(Presenter.DefaultFollowUpWaitMs + 2000));
        await harness.Flush();
        Assert.DoesNotContain(session.Sent, s => s.EventId?.Contains("-resume-") == true);
    }

    [Fact]
    public async Task Final_answer_gives_the_live_model_a_fresh_window_before_the_escape()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();
        var logs = new List<string>();
        harness.Presenter.Log += entry => logs.Add(entry.Message);
        session.Hear("question", 100, 150);
        session.RaiseDelegation("responses", "d");
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromSeconds(14));
        await harness.Flush();
        session.RaiseDelegatedResponse("d", "response.completed");
        await harness.Flush();
        // Plan 005: a backend answer that is ready re-arms the 15 s window so the live model can speak it.
        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        await harness.Flush();
        Assert.DoesNotContain(logs, line => line.Contains("released after 15 s"));
        harness.Clock.Advance(TimeSpan.FromSeconds(14));
        await harness.Flush();
        Assert.Contains(logs, line => line.Contains("released after 15 s"));
    }

    [Fact]
    public async Task Premature_confirmed_true_is_failure_and_only_asks_question()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();
        session.RaiseToolCall("d", "premature", "end_presentation", "{\"confirmed\":true}");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "premature");
        Assert.False(ParseOutput(session, "premature").Ok);
        Assert.Contains(session.Sent, s => s.EventId == "end-confirmation");
        Assert.DoesNotContain(session.Sent, s => s.Type == "close");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public async Task Go_to_slide_out_of_range_returns_error_and_does_not_navigate(int invalidSlideNumber)
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();
        Assert.Equal(0, harness.Presenter.Snapshot().SlideIndex);

        session.RaiseToolCall("del_1", $"c_goto_{invalidSlideNumber}", "go_to_slide", $"{{\"slide_number\": {invalidSlideNumber}}}");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == $"c_goto_{invalidSlideNumber}");

        Assert.Equal(0, harness.Presenter.Snapshot().SlideIndex);
        var output = ParseOutput(session, $"c_goto_{invalidSlideNumber}");
        Assert.False(output.Ok);
        Assert.Equal("there are slides 1 to 3", output.Message);
    }

    [Fact]
    public async Task Deadlock_oracle_tool_calling_presenter_and_button_command_both_complete_within_bound()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();

        // Concurrently invoke tool (which calls NextAsync) and a direct button command (PauseAsync)
        var toolTask = Task.Run(async () =>
        {
            session.RaiseToolCall("del_1", "c_deadlock", "next_slide", "{}");
            await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "c_deadlock");
        });

        var buttonTask = Task.Run(async () =>
        {
            await harness.Presenter.PauseAsync();
        });

        // Explicit bound: both must complete within 3 seconds, proving no loop deadlock
        await Task.WhenAll(toolTask, buttonTask).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Contains(session.Sent, s => s.Type == "tool_output" && s.EventId == "c_deadlock");
    }

    [Fact]
    public async Task Exact_frame_sequence_multi_turn_tool_rounds()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();

        // Frame 0: delegation begins
        session.RaiseDelegation("responses", "del_seq");

        // Frame 1: call
        session.RaiseToolCall("del_seq", "c1", "next_slide", "{}");

        // Frame 2: empty completed
        session.RaiseDelegatedResponse("del_seq", "response.completed");

        // Wait for Frame 3: output / create
        await harness.WaitForSentAsync(s => s.Type == "continue_responses");

        var sentSnapshot1 = session.Sent.ToList();
        var c1OutputIndex = sentSnapshot1.FindIndex(s => s.Type == "tool_output" && s.EventId == "c1");
        var contIndex1 = sentSnapshot1.FindIndex(s => s.Type == "continue_responses");
        Assert.True(c1OutputIndex >= 0, "c1 tool_output must be sent");
        Assert.True(contIndex1 > c1OutputIndex, "continue_responses must follow c1 tool_output");

        // Frame 4: second call
        session.RaiseToolCall("del_seq", "c2", "pause_presentation", "{}");

        // Frame 5: empty completed
        session.RaiseDelegatedResponse("del_seq", "response.completed");

        // Wait for Frame 6: second output / create
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "c2");

        var sentSnapshot2 = session.Sent.ToList();
        var c2OutputIndex = sentSnapshot2.FindIndex(s => s.Type == "tool_output" && s.EventId == "c2");
        var contIndex2 = sentSnapshot2.FindLastIndex(s => s.Type == "continue_responses");
        Assert.True(c2OutputIndex >= 0, "c2 tool_output must be sent");
        Assert.True(contIndex2 > c2OutputIndex, "second continue_responses must follow c2 tool_output");
        Assert.Equal(2, session.Sent.Count(s => s.Type == "continue_responses"));

        // Frame 7: final completed (empty round -> answer ready)
        session.RaiseDelegatedResponse("del_seq", "response.completed");
        await harness.Flush();

        // Tracker should now have closed del_seq
        Assert.False(harness.Presenter.ToolRoundTracker.HasPendingBackendDelegation);
        Assert.Equal(2, session.Sent.Count(s => s.Type == "continue_responses"));
    }

    [Fact]
    public async Task Two_interleaved_delegations_barrier_holds_until_both_outputs_submitted()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();

        session.RaiseDelegation("responses", "del_A");
        session.RaiseDelegation("responses", "del_B");

        // Both delegations make tool calls (using non-navigation tools so slide generation isn't bumped)
        session.RaiseToolCall("del_A", "c_A", "pause_presentation", "{}");
        session.RaiseToolCall("del_B", "c_B", "resume_presentation", "{}");

        // Delegation A completes its round
        session.RaiseDelegatedResponse("del_A", "response.completed");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "c_A");

        // Barrier holds: continue_responses must NOT be sent yet because del_B is still pending!
        Assert.DoesNotContain(session.Sent, s => s.Type == "continue_responses");

        // Delegation B completes its round
        session.RaiseDelegatedResponse("del_B", "response.completed");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "c_B");

        // Now both outputs are submitted and both responses completed -> single continue_responses sent
        await harness.WaitForSentAsync(s => s.Type == "continue_responses");
        Assert.Equal(1, session.Sent.Count(s => s.Type == "continue_responses"));
    }

    [Fact]
    public async Task Tool_failures_exception_timeout_and_malformed_arguments_each_give_exactly_one_ok_false()
    {
        var registry = new ToolRegistry();
        registry.Register(new CrashingTool());
        registry.Register(new SlowTool());

        await using var harness = Create(toolRegistry: registry);
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();

        // 1. Exception -> ok: false, "tool failed"
        session.RaiseToolCall("del_1", "c_crash", "crash_tool", "{}");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "c_crash");
        var crashOutput = ParseOutput(session, "c_crash");
        Assert.False(crashOutput.Ok);
        Assert.Equal("tool failed", crashOutput.Message);
        Assert.Equal(1, session.Sent.Count(s => s.Type == "tool_output" && s.EventId == "c_crash"));

        // 2. Timeout -> ok: false, "timed out"
        session.RaiseToolCall("del_1", "c_timeout", "slow_tool", "{}");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "c_timeout");
        var timeoutOutput = ParseOutput(session, "c_timeout");
        Assert.False(timeoutOutput.Ok);
        Assert.Equal("timed out", timeoutOutput.Message);
        Assert.Equal(1, session.Sent.Count(s => s.Type == "tool_output" && s.EventId == "c_timeout"));

        // 3. Malformed JSON -> ok: false, "malformed argument JSON"
        session.RaiseToolCall("del_1", "c_malformed", "next_slide", "{not json");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "c_malformed");
        var malformedOutput = ParseOutput(session, "c_malformed");
        Assert.False(malformedOutput.Ok);
        Assert.Equal("malformed argument JSON", malformedOutput.Message);
        Assert.Equal(1, session.Sent.Count(s => s.Type == "tool_output" && s.EventId == "c_malformed"));

        // 4. Schema validation failure -> ok: false
        session.RaiseToolCall("del_1", "c_schema", "go_to_slide", "{}");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "c_schema");
        var schemaOutput = ParseOutput(session, "c_schema");
        Assert.False(schemaOutput.Ok);
        Assert.Equal(1, session.Sent.Count(s => s.Type == "tool_output" && s.EventId == "c_schema"));
    }

    [Fact]
    public async Task Ignored_cancellation_times_out_once_and_passes_barrier()
    {
        var gate = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ToolRegistry();
        registry.Register(new GateTool("ignores_cancel", gate.Task, ignoreCancellation: true));
        await using var harness = Create(toolRegistry: registry);
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();
        session.RaiseToolCall("del_timeout", "hung", "ignores_cancel", "{}");
        session.RaiseDelegatedResponse("del_timeout", "response.completed");
        await harness.WaitForSentAsync(s => s.Type == "continue_responses");
        Assert.Equal("timed out", ParseOutput(session, "hung").Message);
        gate.SetResult(ToolResult.Success("late"));
        await Task.Delay(50);
        await harness.Flush();
        Assert.Single(session.Sent, s => s.Type == "tool_output" && s.EventId == "hung");
    }

    [Fact]
    public async Task Timed_out_tool_can_read_cloned_arguments_and_late_fault_is_logged_as_type()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ToolRegistry();
        registry.Register(new LateReadingTool(gate.Task));
        await using var harness = Create(toolRegistry: registry);
        var logs = new List<string>();
        harness.Presenter.Log += entry => logs.Add(entry.Message);
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();
        session.RaiseToolCall("d", "late", "late_reader", "{\"value\":\"intact\"}");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "late");
        Assert.Equal("timed out", ParseOutput(session, "late").Message);
        gate.SetResult();
        for (var i = 0; i < 100 && !logs.Any(x => x.Contains("late tool fault")); i++)
        {
            await Task.Delay(10);
            await harness.Flush();
        }
        Assert.Contains(logs, x => x.Contains("late tool fault") && x.Contains("InvalidOperationException"));
        Assert.DoesNotContain(logs, x => x.Contains("sensitive late value"));
        Assert.Single(session.Sent, s => s.Type == "tool_output" && s.EventId == "late");
    }

    [Fact]
    public async Task Navigation_by_tool_then_button_before_completion_is_stale()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ToolRegistry();
        await using var harness = Create(toolRegistry: registry);
        registry.Register(new NavigateThenGateTool(gate.Task, harness.Presenter));
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();
        session.RaiseToolCall("d", "nav", "navigate_gate", "{}");
        for (var i = 0; i < 100 && harness.Presenter.Snapshot().SlideIndex != 1; i++) await Task.Delay(10);
        Assert.Equal(1, harness.Presenter.Snapshot().SlideIndex);
        Assert.True(await harness.Presenter.NextAsync());
        gate.SetResult();
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "nav");
        Assert.False(ParseOutput(session, "nav").Ok);
        Assert.Contains("stale", ParseOutput(session, "nav").Message);
    }

    [Fact]
    public async Task Tool_completion_after_navigation_is_stale_but_submitted()
    {
        var gate = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ToolRegistry();
        registry.Register(new GateTool("gate_nav", gate.Task));

        await using var harness = Create(toolRegistry: registry);
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();
        Assert.Equal(0, harness.Presenter.Snapshot().SlideIndex);

        // Start gate_nav call
        session.RaiseToolCall("del_1", "c_stale_nav", "gate_nav", "{}");
        await harness.Flush();

        // While tool is in flight, external navigation occurs
        Assert.True(await harness.Presenter.NextAsync());
        Assert.Equal(1, harness.Presenter.Snapshot().SlideIndex);

        // Complete the tool now
        gate.SetResult(ToolResult.Success("finished"));
        await Task.Delay(50);
        await harness.Flush();

        // Same-session navigation must not strand the global tool barrier.
        var output = ParseOutput(session, "c_stale_nav");
        Assert.False(output.Ok);
        Assert.Contains("stale", output.Message);
        session.RaiseDelegatedResponse("del_1", "response.completed");
        await harness.WaitForSentAsync(s => s.Type == "continue_responses");
        session.RaiseToolCall("del_1", "later", "pause_presentation", "{}");
        session.RaiseDelegatedResponse("del_1", "response.completed");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "later");
        Assert.True(ParseOutput(session, "later").Ok);
    }

    [Fact]
    public async Task Refused_output_does_not_mark_call_submitted_or_pass_barrier()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();
        session.RefuseToolOutput = true;
        session.RaiseToolCall("del_refused", "refused", "pause_presentation", "{}");
        session.RaiseDelegatedResponse("del_refused", "response.completed");
        await Task.Delay(50);
        await harness.Flush();
        Assert.True(harness.Presenter.ToolRoundTracker.IsCallPending("del_refused", "refused"));
        Assert.DoesNotContain(session.Sent, s => s.Type == "continue_responses");
    }

    [Fact]
    public async Task Refused_continue_does_not_advance_round()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();
        session.RefuseContinue = true;
        session.RaiseToolCall("del_refused", "refused", "pause_presentation", "{}");
        session.RaiseDelegatedResponse("del_refused", "response.completed");
        await harness.WaitForSentAsync(s => s.Type == "tool_output");
        Assert.True(harness.Presenter.ToolRoundTracker.ShouldSendContinueResponses());
    }

    [Fact]
    public async Task Tool_call_queued_while_ending_is_rejected()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();
        session.CloseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ending = harness.Presenter.EndAsync();
        await Task.Delay(30);
        Assert.Contains(session.Sent, s => s.Type == "close");
        session.RaiseToolCall("late", "late", "next_slide", "{}");
        session.CloseGate.SetResult();
        await ending;
        await harness.Flush();
        Assert.DoesNotContain(session.Sent, s => s.Type == "tool_output" && s.EventId == "late");
        Assert.False(harness.Presenter.ToolRoundTracker.HasPendingBackendDelegation);
    }

    [Fact]
    public async Task Two_navigation_calls_from_interleaved_delegations_both_get_outputs()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();
        session.RaiseToolCall("d1", "n1", "next_slide", "{}");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "n1");
        session.RaiseToolCall("d2", "n2", "next_slide", "{}");
        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "n2");
        session.RaiseDelegatedResponse("d1", "response.completed");
        session.RaiseDelegatedResponse("d2", "response.completed");
        await harness.WaitForSentAsync(s => s.Type == "continue_responses");
        Assert.Single(session.Sent, s => s.Type == "continue_responses");
        Assert.Equal(2, harness.Presenter.Snapshot().SlideIndex);
    }

    [Fact]
    public async Task Tool_completion_after_end_is_dropped()
    {
        var gate = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ToolRegistry();
        registry.Register(new GateTool("gate_end", gate.Task));

        await using var harness = Create(toolRegistry: registry);
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();

        session.RaiseToolCall("del_1", "c_stale_end", "gate_end", "{}");
        await harness.Flush();

        // Presentation ends
        Assert.True(await harness.Presenter.EndAsync());
        await harness.Flush();
        Assert.Equal("idle", harness.Presenter.Snapshot().State);

        // Complete the tool now
        gate.SetResult(ToolResult.Success("finished"));
        await Task.Delay(50);
        await harness.Flush();

        Assert.DoesNotContain(session.Sent, s => s.Type == "tool_output" && s.EventId == "c_stale_end");
    }

    [Fact]
    public async Task Tool_completion_after_restart_is_dropped()
    {
        var gate = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ToolRegistry();
        registry.Register(new GateTool("gate_restart", gate.Task));

        await using var harness = Create(toolRegistry: registry);
        await harness.Presenter.StartAsync("p");
        var firstSession = harness.Session();
        firstSession.RaiseToolCall("del_1", "c_stale_restart", "gate_restart", "{}");
        await harness.Flush();

        // End presentation and start new presentation run
        await harness.Presenter.EndAsync();
        await harness.Presenter.StartAsync("p");
        var secondSession = harness.Session();
        Assert.NotSame(firstSession, secondSession);

        // Complete the tool from the first run
        gate.SetResult(ToolResult.Success("finished"));
        await Task.Delay(50);
        await harness.Flush();

        Assert.DoesNotContain(firstSession.Sent, s => s.Type == "tool_output" && s.EventId == "c_stale_restart");
        Assert.DoesNotContain(secondSession.Sent, s => s.Type == "tool_output" && s.EventId == "c_stale_restart");
    }

    [Fact]
    public async Task Tool_completion_after_takeover_is_dropped()
    {
        var gate = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ToolRegistry();
        registry.Register(new GateTool("gate_takeover", gate.Task));

        await using var harness = Create(toolRegistry: registry);
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();

        session.RaiseToolCall("del_1", "c_stale_takeover", "gate_takeover", "{}");
        await harness.Flush();

        // Take over ends the session resumably
        Assert.True(await harness.Presenter.EndAsync(resumable: true));
        await harness.Flush();
        Assert.Equal("idle", harness.Presenter.Snapshot().State);

        // Complete the tool
        gate.SetResult(ToolResult.Success("finished"));
        await Task.Delay(50);
        await harness.Flush();

        Assert.DoesNotContain(session.Sent, s => s.Type == "tool_output" && s.EventId == "c_stale_takeover");
    }

    [Fact]
    public async Task Duplicate_call_ids_are_ignored()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();

        session.RaiseToolCall("del_1", "call_dup", "next_slide", "{}");
        session.RaiseToolCall("del_1", "call_dup", "next_slide", "{}");

        await harness.WaitForSentAsync(s => s.Type == "tool_output" && s.EventId == "call_dup");
        await Task.Delay(50);
        await harness.Flush();

        Assert.Equal(1, session.Sent.Count(s => s.Type == "tool_output" && s.EventId == "call_dup"));
        Assert.Equal(1, harness.Presenter.Snapshot().SlideIndex);
    }

    [Fact]
    public async Task Question_hold_is_not_released_on_tool_round_completion()
    {
        await using var harness = Create();
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();

        // Audience asks a question -> hold opens
        session.Hear("Can you pause?");
        session.RaiseDelegation("responses", "del_q");
        await harness.Flush();

        // Tool round occurs
        session.RaiseToolCall("del_q", "c_tool", "pause_presentation", "{}");
        session.RaiseDelegatedResponse("del_q", "response.completed");
        await harness.WaitForSentAsync(s => s.Type == "continue_responses");

        // Advance clock past advance silence / part gap
        harness.Clock.Advance(TimeSpan.FromMilliseconds(3000));
        await harness.Flush();

        // Question hold is STILL OPEN because backend delegation has not finished its final answer!
        Assert.True(harness.Presenter.ToolRoundTracker.HasPendingBackendDelegation);

        // Now final answer completed
        session.RaiseDelegatedResponse("del_q", "response.completed");
        await harness.Flush();
        Assert.False(harness.Presenter.ToolRoundTracker.HasPendingBackendDelegation);

        // Model speaks answer
        session.Speak(500);
        await harness.Flush();

        // End follow-up wait
        await harness.EndFollowUp();
    }

    [Fact]
    public async Task Tool_round_rearms_hold_while_backend_spans_fifteen_seconds()
    {
        var gate = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ToolRegistry();
        registry.Register(new GateTool("long_backend", gate.Task));
        await using var harness = Create(toolRegistry: registry);
        await harness.Presenter.StartAsync("p");
        var session = harness.Session();
        var logs = new List<string>();
        harness.Presenter.Log += entry => logs.Add(entry.Message);
        session.Hear("question", 100, 150);
        session.RaiseDelegation("responses", "slow_backend");
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromSeconds(8));
        await harness.Flush();
        session.RaiseToolCall("slow_backend", "call", "long_backend", "{}");
        await harness.Flush();
        harness.Clock.Advance(TimeSpan.FromSeconds(8));
        await harness.Flush();
        gate.SetResult(ToolResult.Success("done"));
        session.RaiseDelegatedResponse("slow_backend", "response.completed");
        await harness.WaitForSentAsync(s => s.Type == "continue_responses");
        harness.Clock.Advance(TimeSpan.FromSeconds(8));
        await harness.Flush();
        Assert.Equal(0, harness.Presenter.Snapshot().SlideIndex);
        Assert.DoesNotContain(session.Sent, s => s.EventId?.Contains("-resume-") == true);
        Assert.True(harness.Presenter.ToolRoundTracker.HasPendingBackendDelegation);
        Assert.DoesNotContain(logs, line => line.Contains("released after 15 s"));
    }

    [Fact]
    public void Backend_instructions_includes_slide_titles_and_tool_policy()
    {
        var slides = new List<SlideRef>
        {
            new(0, "Introduction"),
            new(1, "Deep Dive"),
            new(2, "Conclusion")
        };

        var instructions = PromptBuilder.BackendInstructions("My Cool Talk", slides);
        Assert.Contains("Answer audience questions about the talk titled \"My Cool Talk\"", instructions);
        Assert.Contains("Use the tools for any request to pause, continue, move or end; never claim an action the tool did not confirm.", instructions);
        Assert.Contains("1. Introduction", instructions);
        Assert.Contains("2. Deep Dive", instructions);
        Assert.Contains("3. Conclusion", instructions);
    }

    [Fact]
    public async Task Managed_mode_configures_request_tools_and_delegation_instructions()
    {
        // 1. Without delegation model: Tools and DelegationInstructions are null
        await using (var unmanaged = Create(hasDelegationModel: _ => false))
        {
            await unmanaged.Presenter.StartAsync("p");
            var req = unmanaged.Session().Request;
            Assert.NotNull(req);
            Assert.Null(req.Tools);
            Assert.Null(req.DelegationInstructions);
        }

        // 2. With delegation model: Tools and DelegationInstructions are populated
        await using (var managed = Create(hasDelegationModel: _ => true))
        {
            await managed.Presenter.StartAsync("p");
            var req = managed.Session().Request;
            Assert.NotNull(req);
            Assert.NotNull(req.Tools);
            Assert.Equal(6, req.Tools.Count);
            Assert.NotNull(req.DelegationInstructions);
            Assert.Contains("Use the tools for any request", req.DelegationInstructions);
        }
    }

    private static (bool Ok, string Message) ParseOutput(FakeSession session, string callId)
    {
        var item = session.Sent.First(s => s.Type == "tool_output" && s.EventId == callId);
        using var doc = JsonDocument.Parse(item.Content!);
        return (
            doc.RootElement.GetProperty("ok").GetBoolean(),
            doc.RootElement.GetProperty("message").GetString()!
        );
    }

    private static readonly Slide[] DefaultSlides =
    [
        new(0, 1, "One", "First slide text.", "n1"),
        new(1, 2, "Two", "Second slide text.", string.Empty),
        new(2, 3, "Three", "Third slide text.", "n3")
    ];

    private static Harness Create(
        int upstreams = 1,
        IReadOnlyList<Slide>? slides = null,
        int advanceSilenceMs = 2000,
        int chunkChars = 200,
        ToolRegistry? toolRegistry = null,
        Func<int, bool>? hasDelegationModel = null,
        int followUpWaitMs = Presenter.DefaultFollowUpWaitMs)
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
                    Request = request
                };
                sessions.Add(session);
                return session;
            },
            (_, id, _) => Task.FromResult(new LoadedPresentation(
                id,
                new PresentationMeta(id, "T", "deck", "showFn", null, null, advanceSilenceMs, chunkChars),
                slides ?? DefaultSlides,
                "ctx")),
            new PresenterSettings(advanceSilenceMs, "marin", followUpWaitMs, ToolsOptions.DefaultMaxInlineTools),
            clock,
            toolRegistry,
            hasDelegationModel);
        return new Harness(presenter, clock, sessions);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(Presenter presenter, FakeTimeProvider clock, List<FakeSession> sessions)
        {
            Presenter = presenter;
            Clock = clock;
            Sessions = sessions;
            presenter.Slide += Slides.Add;
        }

        public Presenter Presenter { get; }
        public FakeTimeProvider Clock { get; }
        public List<FakeSession> Sessions { get; }
        public List<int> Slides { get; } = [];

        public FakeSession Session() => Sessions[^1];

        public Task Flush() => Presenter.WaitUntilIdleAsync();

        public async Task EndFollowUp(int milliseconds = Presenter.DefaultFollowUpWaitMs + 1)
        {
            Clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
            await Flush();
        }

        public async Task WaitForSentAsync(Func<(string Type, string? Content, string? EventId, string? DelegationId), bool> predicate, int timeoutMs = 3000)
        {
            var start = Environment.TickCount64;
            while (!Session().Sent.Any(predicate))
            {
                if (Environment.TickCount64 - start > timeoutMs)
                {
                    throw new TimeoutException($"Timed out waiting for Sent predicate. Currently Sent: {string.Join(", ", Session().Sent.Select(s => $"{s.Type}:{s.EventId}"))}");
                }
                await Task.Delay(10);
            }
            await Flush();
        }

        public ValueTask DisposeAsync() => Presenter.DisposeAsync();
    }

    private sealed class LateReadingTool(Task gate) : ITool
    {
        public string Name => "late_reader";
        public string Description => "Reads late";
        public TimeSpan Timeout => TimeSpan.FromMilliseconds(50);
        public JsonObject Parameters { get; } = new() { ["type"] = "object", ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "string" } } };
        public IReadOnlyList<string> Tags => [];
        public bool Pinned => false;
        public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            await gate;
            if (arguments.GetProperty("value").GetString() != "intact") throw new Exception("arguments disposed");
            throw new InvalidOperationException("sensitive late value");
        }
    }

    private sealed class NavigateThenGateTool(Task gate, IPresenter presenter) : ITool
    {
        public string Name => "navigate_gate";
        public string Description => "Navigates and waits";
        public JsonObject Parameters { get; } = new() { ["type"] = "object", ["properties"] = new JsonObject() };
        public IReadOnlyList<string> Tags => [];
        public bool Pinned => false;
        public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            await presenter.NextAsync(cancellationToken);
            await gate;
            return ToolResult.Success("moved to slide 2");
        }
    }

    private sealed class CrashingTool : ITool
    {
        public string Name => "crash_tool";
        public string Description => "A tool that throws an unhandled exception.";
        public JsonObject Parameters { get; } = new() { ["type"] = "object", ["properties"] = new JsonObject() };
        public IReadOnlyList<string> Tags { get; } = ["test"];
        public bool Pinned => false;

        public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class SlowTool : ITool
    {
        public string Name => "slow_tool";
        public string Description => "A tool that takes a long time to complete.";
        public JsonObject Parameters { get; } = new() { ["type"] = "object", ["properties"] = new JsonObject() };
        public IReadOnlyList<string> Tags { get; } = ["test"];
        public bool Pinned => false;
        public TimeSpan Timeout => TimeSpan.FromMilliseconds(50);

        public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            await Task.Delay(500, cancellationToken);
            return ToolResult.Success("finished");
        }
    }

    private sealed class GateTool : ITool
    {
        private readonly Task<ToolResult> _task;

        private readonly bool _ignoreCancellation;

        public GateTool(string name, Task<ToolResult> task, bool ignoreCancellation = false)
        {
            Name = name;
            _task = task;
            _ignoreCancellation = ignoreCancellation;
        }

        public TimeSpan Timeout => _ignoreCancellation ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromSeconds(5);

        public string Name { get; }
        public string Description => "A gate tool.";
        public JsonObject Parameters { get; } = new() { ["type"] = "object", ["properties"] = new JsonObject() };
        public IReadOnlyList<string> Tags { get; } = ["test"];
        public bool Pinned => false;

        public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            return await (_ignoreCancellation ? _task : _task.WaitAsync(cancellationToken));
        }
    }
}
