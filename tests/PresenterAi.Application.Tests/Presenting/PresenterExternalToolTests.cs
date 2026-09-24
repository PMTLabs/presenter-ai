using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using System.Collections.Concurrent;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Scripts;
using PresenterAi.Application.Tools;
using PresenterAi.Application.Tools.External;
using Xunit;

namespace PresenterAi.Application.Tests.Presenting;

public sealed class PresenterExternalToolTests
{
    private static readonly IReadOnlyList<Slide> Slides = [new(0, 1, "One", "one", null), new(1, 2, "Two", "two", null), new(2, 3, "Three", "three", null)];

    [Fact]
    public async Task Loader_runs_in_parallel_and_late_set_is_disposed_once()
    {
        var presentation = new TaskCompletionSource<LoadedPresentation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tools = new TaskCompletionSource<SessionToolSet>(TaskCreationOptions.RunContinuationsAsynchronously);
        var set = new CountingSet();
        await using var h = new Harness((_, _, _) => presentation.Task, (_, _) => tools.Task, TimeSpan.FromMilliseconds(30));
        var start = h.Presenter.StartAsync("p", null, "owner");
        try
        {
            // The tool source is asked while the presentation load is still pending: the loads run in parallel.
            await Eventually(() => h.Owner == "owner");
            Assert.False(start.IsCompleted);
        }
        finally
        {
            // Released even when the wait fails: the loader ignores cancellation, so a pending load would wedge the
            // loop and hang disposal.
            presentation.TrySetResult(Harness.Presentation());
        }
        Assert.True((await start).Started);
        Assert.DoesNotContain(h.Session!.Request!.Tools!, t => t["name"]?.ToString() == "external_action");
        tools.SetResult(new SessionToolSet([new ExternalTool()], disposable: set));
        await Eventually(() => set.Count == 1);
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public async Task Source_fault_after_cap_is_observed_without_sensitive_error_message()
    {
        var tcs = new TaskCompletionSource<SessionToolSet>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = new Harness((_, id, _) => Task.FromResult(Harness.Presentation()), (_, _) => tcs.Task, TimeSpan.FromMilliseconds(30));
        Assert.True((await h.Start()).Started);
        tcs.SetException(new InvalidOperationException("private payload"));
        await Eventually(() => h.Logs.Any(x => x.Contains("InvalidOperationException")));
        Assert.DoesNotContain(h.Logs, x => x.Contains("private payload"));
    }

    [Fact]
    public async Task Source_returning_just_after_cap_is_disposed_once()
    {
        var disposable = new CountingSet();
        await using var h = new Harness((_, id, _) => Task.FromResult(Harness.Presentation()),
            async (_, _) => { await Task.Delay(45); return new SessionToolSet([new ExternalTool()], disposable: disposable); },
            TimeSpan.FromMilliseconds(30));
        Assert.True((await h.Start()).Started);
        Assert.DoesNotContain(h.Session!.Request!.Tools!, t => t["name"]?.ToString() == "external_action");
        await Eventually(() => disposable.Count == 1);
        Assert.Equal(1, disposable.Count);
    }

    [Fact]
    public async Task Hostile_tool_labels_are_escaped_and_capped_in_page_log()
    {
        var hostileName = "bad\nFAKE LOG\u0001" + new string('x', 2048);
        var tool = new ExternalTool
        {
            Ask = false,
            NameValue = hostileName,
            SourceValue = "server\r\nforged"
        };
        await using var h = new Harness((_, _, _) => Task.FromResult(Harness.Presentation()),
            (_, _) => Task.FromResult(new SessionToolSet([tool])));
        await h.Start();
        h.Session!.RaiseToolCall("d", "hostile-call", tool.Name, "{}");
        await Eventually(() => h.Session.Sent.Any(x => x.EventId == "hostile-call"));
        var capturedLogs = h.Logs.ToArray();
        Assert.All(capturedLogs, x =>
        {
            Assert.DoesNotContain('\n', x);
            Assert.DoesNotContain('\r', x);
            Assert.True(x.Length <= 1024);
        });
        Assert.Contains(capturedLogs, x => x.Contains("\\u000a", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_only_tool_runs_once_and_page_log_contains_neither_arguments_nor_result()
    {
        var tool = new ExternalTool { Ask = false };
        await using var h = new Harness((_, id, _) => Task.FromResult(Harness.Presentation()), (_, _) => Task.FromResult(new SessionToolSet([tool], notes: ["fake tools loaded"])));
        await h.Start();
        Assert.Contains("fake tools loaded", h.Logs);
        var s = h.Session!;
        s.RaiseToolCall("d", "c", "external_action", "{}");
        s.RaiseToolCall("d", "c", "external_action", "{}");
        await Eventually(() => s.Sent.Any(x => x.EventId == "c"));
        await h.Presenter.WaitUntilIdleAsync();
        Assert.Single(s.Sent, x => x.EventId == "c");
        Assert.Equal(1, tool.Count);
        Assert.DoesNotContain(h.Logs, x => x.Contains("done"));
        s.RaiseHostedActivity("d", "completed");
        await h.Presenter.WaitUntilIdleAsync();
        Assert.Contains(h.Logs, x => x.Contains("web search: done"));
    }

    [Fact]
    public async Task Faulting_source_is_logged_as_type_and_start_continues()
    {
        await using var h = new Harness((_, _, _) => Task.FromResult(Harness.Presentation()), (_, _) => Task.FromException<SessionToolSet>(new InvalidOperationException("private argument")));
        Assert.True((await h.Start()).Started);
        Assert.Contains(h.Logs, x => x.Contains("InvalidOperationException"));
        Assert.DoesNotContain(h.Logs, x => x.Contains("private argument"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gate_resolves_direct_and_call_tool_before_asking_and_never_executes_from_model(bool meta)
    {
        var tool = new ExternalTool();
        await using var h = new Harness((_, id, _) => Task.FromResult(Harness.Presentation()), (_, _) => Task.FromResult(new SessionToolSet([tool])), maxInline: meta ? 1 : 16);
        await h.Start();
        var session = h.Session!;
        var name = meta ? "call_tool" : "external_action";
        var args = meta ? "{\"name\":\"external_action\",\"arguments\":{}}" : "{}";
        session.RaiseDelegation("responses", "d1");
        session.RaiseToolCall("d1", "c1", name, args);
        session.RaiseToolCall("d1", "c1", name, args);
        session.RaiseDelegatedResponse("d1");
        await Eventually(() => session.Sent.Any(s => s.Type == "tool_output"));
        await h.Presenter.WaitUntilIdleAsync();
        Assert.Single(session.Sent, s => s.Type == "tool_output" && s.EventId == "c1");
        var required = JsonNode.Parse(session.Sent.Single(s => s.Type == "tool_output").Content!)!;
        Assert.Equal("confirmation_required", required["status"]?.ToString());
        Assert.Contains("Shall I", required["question"]?.ToString());
        Assert.Equal(0, tool.Count);
        session.RaiseToolCall("d1", "c2", name, args);
        await Eventually(() => session.Sent.Any(s => s.EventId == "c2"));
        Assert.Equal("confirmation_pending", JsonNode.Parse(session.Sent.Single(s => s.EventId == "c2").Content!)?["status"]?.ToString());
        Assert.Equal(0, tool.Count);
    }

    [Fact]
    public async Task Different_action_is_rejected_while_one_confirmation_is_pending()
    {
        var second = new OtherExternalTool();
        await using var h = new Harness((_, id, _) => Task.FromResult(Harness.Presentation()), (_, _) => Task.FromResult(new SessionToolSet([new ExternalTool(), second])));
        await h.Start();
        var s = h.Session!;
        s.RaiseToolCall("d1", "c1", "external_action", "{}");
        s.RaiseToolCall("d2", "c2", "other_action", "{}");
        await Eventually(() => s.Sent.Any(x => x.EventId == "c2"));
        Assert.Contains("another action is waiting", s.Sent.Single(x => x.EventId == "c2").Content);
        Assert.Equal(0, second.Count);
    }

    [Fact]
    public async Task Yes_overlapping_model_speech_does_not_approve()
    {
        var tool = new ExternalTool();
        await using var h = new Harness((_, id, _) => Task.FromResult(Harness.Presentation()), (_, _) => Task.FromResult(new SessionToolSet([tool])));
        await h.Start();
        var s = h.Session!;
        s.RaiseToolCall("d", "c", "external_action", "{}");
        await Eventually(() => s.Sent.Any(x => x.EventId == "c"));
        // The question is armed after its output is sent: settle before moving the clock past it.
        await h.Presenter.WaitUntilIdleAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(8)); await h.Presenter.WaitUntilIdleAsync();
        s.Speak(startMs: 2000, endMs: 2100);
        s.Hear("yes", 2000, 2100);
        await h.Presenter.WaitUntilIdleAsync();
        h.Clock.Advance(TimeSpan.FromMilliseconds(701)); await h.Presenter.WaitUntilIdleAsync();
        await Task.Delay(30);
        Assert.Equal(0, tool.Count);
    }

    [Fact]
    public async Task Immediate_confirmation_outputs_use_tracker_global_barrier_across_interleaved_delegations()
    {
        await using var h = new Harness((_, id, _) => Task.FromResult(Harness.Presentation()), (_, _) => Task.FromResult(new SessionToolSet([new ExternalTool()])));
        await h.Start();
        var s = h.Session!;
        s.RaiseDelegation("responses", "d1");
        s.RaiseDelegation("responses", "d2");
        s.RaiseToolCall("d1", "c1", "external_action", "{}");
        s.RaiseToolCall("d2", "c2", "external_action", "{}");
        s.RaiseToolCall("d2", "c2", "external_action", "{}");
        s.RaiseDelegatedResponse("d1");
        s.RaiseDelegatedResponse("d2");
        await Eventually(() => s.Sent.Count(x => x.Type == "tool_output") == 2);
        await h.Presenter.WaitUntilIdleAsync();
        Assert.Single(s.Sent, x => x.EventId == "c1");
        Assert.Single(s.Sent, x => x.EventId == "c2");
        Assert.Single(s.Sent, x => x.Type == "continue_responses");
    }

    [Theory]
    [InlineData("confirmation_required")]
    [InlineData("confirmation_pending")]
    [InlineData("another")]
    [InlineData("running")]
    [InlineData("cached")]
    public async Task Every_immediate_branch_deduplicates_calls_and_uses_one_global_barrier(string branch)
    {
        var tool = new ExternalTool();
        if (branch is "running" or "cached") tool.Gate = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = new Harness((_, id, _) => Task.FromResult(Harness.Presentation()), (_, _) => Task.FromResult(new SessionToolSet([tool, new OtherExternalTool()])));
        await h.Start();
        var s = h.Session!;
        s.RaiseDelegation("responses", "d0");
        s.RaiseToolCall("d0", "init", "external_action", "{}");
        await Eventually(() => s.Sent.Any(x => x.EventId == "init"));
        s.RaiseDelegatedResponse("d0");
        await Eventually(() => s.Sent.Any(x => x.Type == "continue_responses"));
        if (branch is "running" or "cached")
        {
            h.Clock.Advance(TimeSpan.FromSeconds(8)); await h.Presenter.WaitUntilIdleAsync();
            s.Hear("yes", 2000, 2100); await h.Presenter.WaitUntilIdleAsync();
            h.Clock.Advance(TimeSpan.FromMilliseconds(701)); await h.Presenter.WaitUntilIdleAsync();
            await Eventually(() => tool.Count == 1);
            if (branch == "cached")
            {
                tool.Gate!.SetResult(ToolResult.Success("done"));
                await Eventually(() => s.Sent.Any(x => x.Type == "commentary"));
            }
        }
        var before = s.Sent.Count(x => x.Type == "continue_responses");
        s.RaiseDelegation("responses", "d1");
        s.RaiseDelegation("responses", "d2");
        var name = branch == "another" ? "other_action" : "external_action";
        s.RaiseToolCall("d1", "c1", name, "{}");
        s.RaiseToolCall("d1", "c1", name, "{}");
        s.RaiseToolCall("d2", "c2", name, "{}");
        s.RaiseToolCall("d2", "c2", name, "{}");
        s.RaiseDelegatedResponse("d1");
        s.RaiseDelegatedResponse("d2");
        await Eventually(() => s.Sent.Any(x => x.EventId == "c2"));
        await h.Presenter.WaitUntilIdleAsync();
        Assert.Single(s.Sent, x => x.EventId == "c1");
        Assert.Single(s.Sent, x => x.EventId == "c2");
        Assert.Equal(before + 1, s.Sent.Count(x => x.Type == "continue_responses"));
        if (branch == "another") Assert.Contains("another action", s.Sent.Single(x => x.EventId == "c1").Content);
        if (branch is "running" or "cached")
            Assert.Contains(branch == "running" ? "running" : "done", s.Sent.Single(x => x.EventId == "c1").Content);
        if (branch == "confirmation_pending") Assert.Contains("confirmation_pending", s.Sent.Single(x => x.EventId == "c1").Content);
    }

    [Fact]
    public async Task Yes_runs_locally_and_a_second_delegation_gets_running_then_cached_output()
    {
        var tool = new ExternalTool { Gate = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var h = new Harness((_, id, _) => Task.FromResult(Harness.Presentation()), (_, _) => Task.FromResult(new SessionToolSet([tool])));
        await h.Start();
        var s = h.Session!;
        s.RaiseToolCall("d1", "c1", "external_action", "{}");
        await Eventually(() => s.Sent.Any(x => x.EventId == "c1"));
        await h.Presenter.WaitUntilIdleAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(8)); await h.Presenter.WaitUntilIdleAsync();
        s.Hear("yes", 2000, 2100);
        await h.Presenter.WaitUntilIdleAsync();
        h.Clock.Advance(TimeSpan.FromMilliseconds(701)); await h.Presenter.WaitUntilIdleAsync();
        await Eventually(() => tool.Count == 1);
        s.RaiseToolCall("d2", "c2", "external_action", "{}");
        await Eventually(() => s.Sent.Any(x => x.EventId == "c2"));
        Assert.Contains("running", s.Sent.Single(x => x.EventId == "c2").Content);
        tool.Gate.SetResult(ToolResult.Success("private result"));
        await Eventually(() => s.Sent.Any(x => x.Type == "commentary"));
        await h.Presenter.WaitUntilIdleAsync();
        Assert.Contains(s.Sent, x => x.Type == "thinking");
        s.RaiseToolCall("d2", "c3", "external_action", "{}");
        await Eventually(() => s.Sent.Any(x => x.EventId == "c3"));
        Assert.Contains("private result", s.Sent.Single(x => x.EventId == "c3").Content);
    }

    [Theory]
    [InlineData("next")]
    [InlineData("prev")]
    [InlineData("goto")]
    [InlineData("restart")]
    [InlineData("end")]
    [InlineData("takeover")]
    public async Task Approved_run_after_moving_on_is_not_announced(string action)
    {
        var tool = new ExternalTool { Gate = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var h = new Harness((_, id, _) => Task.FromResult(Harness.Presentation()), (_, _) => Task.FromResult(new SessionToolSet([tool])));
        await h.Start();
        var s = h.Session!;
        s.RaiseToolCall("d", "c", "external_action", "{}");
        await Eventually(() => s.Sent.Any(x => x.EventId == "c"));
        await h.Presenter.WaitUntilIdleAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(8)); await h.Presenter.WaitUntilIdleAsync();
        s.Hear("yes", 2000, 2100);
        await h.Presenter.WaitUntilIdleAsync();
        h.Clock.Advance(TimeSpan.FromMilliseconds(701)); await h.Presenter.WaitUntilIdleAsync();
        await Eventually(() => tool.Count == 1);
        switch (action)
        {
            case "next": await h.Presenter.NextAsync(); break;
            case "prev": await h.Presenter.NextAsync(); await h.Presenter.PrevAsync(); break;
            case "goto": await h.Presenter.GotoAsync(2); break;
            case "takeover": await h.Presenter.EndAsync(resumable: true); break;
            default: await h.Presenter.EndAsync(); if (action == "restart") await h.Start(); break;
        }
        tool.Gate.SetResult(ToolResult.Success("secret answer"));
        await Eventually(() => h.Logs.Any(x => x.Contains("not announced")));
        Assert.DoesNotContain(s.Sent, x => x.Type is "thinking" or "commentary");
    }

    [Fact]
    public async Task Models_yes_is_not_a_local_approval()
    {
        var tool = new ExternalTool();
        await using var h = new Harness((_, id, _) => Task.FromResult(Harness.Presentation()), (_, _) => Task.FromResult(new SessionToolSet([tool])));
        await h.Start();
        var s = h.Session!;
        s.RaiseToolCall("d", "c", "external_action", "{}");
        await Eventually(() => s.Sent.Any(x => x.EventId == "c"));
        await h.Presenter.WaitUntilIdleAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(8)); await h.Presenter.WaitUntilIdleAsync();
        s.ModelTranscript("yes", 2000, 2100);
        await h.Presenter.WaitUntilIdleAsync();
        h.Clock.Advance(TimeSpan.FromMilliseconds(701));
        await h.Presenter.WaitUntilIdleAsync();
        await Task.Delay(30);
        Assert.Equal(0, tool.Count);
    }

    [Fact]
    public async Task End_releases_tool_set_once()
    {
        var disposable = new CountingSet();
        await using var h = new Harness((_, id, _) => Task.FromResult(Harness.Presentation()), (_, _) => Task.FromResult(new SessionToolSet([new ExternalTool()], disposable: disposable)));
        await h.Start();
        await h.Presenter.EndAsync();
        await Eventually(() => disposable.Count == 1);
        Assert.Equal(1, disposable.Count);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("next")]
    [InlineData("prev")]
    [InlineData("goto")]
    [InlineData("voice-next")]
    [InlineData("voice-prev")]
    [InlineData("voice-goto")]
    [InlineData("voice-pause")]
    [InlineData("voice-resume")]
    [InlineData("end")]
    [InlineData("takeover")]
    public async Task Any_control_cancels_pending_tool_before_doing_its_action(string action)
    {
        var tool = new ExternalTool();
        var disposable = new CountingSet();
        await using var h = new Harness((_, id, _) => Task.FromResult(Harness.Presentation()), (_, _) => Task.FromResult(new SessionToolSet([tool], disposable: disposable)));
        await h.Start();
        var s = h.Session!;
        s.RaiseToolCall("d", "c", "external_action", "{}");
        await Eventually(() => s.Sent.Any(x => x.EventId == "c"));
        switch (action)
        {
            case "pause": await h.Presenter.PauseAsync(); break;
            case "resume": await h.Presenter.ResumeAsync(); break;
            case "next": await h.Presenter.NextAsync(); break;
            case "prev": await h.Presenter.NextAsync(); await h.Presenter.PrevAsync(); break;
            case "goto": await h.Presenter.GotoAsync(2); break;
            case "end": await h.Presenter.EndAsync(); break;
            case "takeover": await h.Presenter.EndAsync(resumable: true); break;
            default:
                var utterance = action switch { "voice-next" => "next slide", "voice-prev" => "previous slide", "voice-goto" => "slide 3", "voice-pause" => "pause", _ => "continue" };
                s.Hear(utterance, 2000, 2100);
                await h.Presenter.WaitUntilIdleAsync();
                h.Clock.Advance(TimeSpan.FromMilliseconds(701));
                await h.Presenter.WaitUntilIdleAsync();
                break;
        }
        if (action is not ("end" or "takeover"))
        {
            s.RaiseToolCall("d", "c2", "external_action", "{}");
            await Eventually(() => s.Sent.Any(x => x.EventId == "c2"));
            Assert.Contains("confirmation_required", s.Sent.Single(x => x.EventId == "c2").Content);
        }
        else await Eventually(() => disposable.Count == 1);
        Assert.Equal(0, tool.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Button_end_during_either_confirmation_phase_cancels_without_running_tool(bool answerPhase)
    {
        var tool = new ExternalTool();
        var disposable = new CountingSet();
        await using var h = new Harness((_, id, _) => Task.FromResult(Harness.Presentation()), (_, _) => Task.FromResult(new SessionToolSet([tool], disposable: disposable)));
        await h.Start();
        var s = h.Session!;
        s.RaiseToolCall("d", "c", "external_action", "{}");
        await Eventually(() => s.Sent.Any(x => x.EventId == "c"));
        await h.Presenter.WaitUntilIdleAsync();
        if (answerPhase) { h.Clock.Advance(TimeSpan.FromSeconds(8)); await h.Presenter.WaitUntilIdleAsync(); }
        await h.Presenter.EndAsync();
        await Eventually(() => disposable.Count == 1);
        Assert.Equal(0, tool.Count);
    }

    [Fact]
    public async Task Voice_end_cancels_tool_confirmation_and_starts_end_confirmation()
    {
        var tool = new ExternalTool();
        await using var h = new Harness((_, id, _) => Task.FromResult(Harness.Presentation()), (_, _) => Task.FromResult(new SessionToolSet([tool])));
        await h.Start();
        var s = h.Session!;
        s.RaiseToolCall("d", "c", "external_action", "{}");
        await Eventually(() => s.Sent.Any(x => x.EventId == "c"));
        s.Hear("end meeting", 2000, 2100);
        await h.Presenter.WaitUntilIdleAsync();
        h.Clock.Advance(TimeSpan.FromMilliseconds(701)); await h.Presenter.WaitUntilIdleAsync();
        Assert.Contains(s.Sent, x => x.Type == "instructions" && x.Content?.Contains("Shall I end") == true);
        Assert.Equal(0, tool.Count);
    }

    [Fact]
    public async Task Pending_confirmation_is_cancelled_by_disconnect()
    {
        var tool = new ExternalTool();
        var disposable = new CountingSet();
        await using var h = new Harness((_, id, _) => Task.FromResult(Harness.Presentation()), (_, _) => Task.FromResult(new SessionToolSet([tool], disposable: disposable)));
        await h.Start();
        var s = h.Session!;
        s.RaiseToolCall("d", "c", "external_action", "{}");
        await Eventually(() => s.Sent.Any(x => x.EventId == "c"));
        s.Drop();
        await h.Presenter.WaitUntilIdleAsync();
        await Eventually(() => disposable.Count == 1);
        Assert.Equal(0, tool.Count);
    }

    [Theory]
    [InlineData("no")]
    [InlineData("deadline")]
    [InlineData("button")]
    public async Task Pending_confirmation_can_be_declined_or_cancelled(string action)
    {
        var tool = new ExternalTool();
        await using var h = new Harness((_, id, _) => Task.FromResult(Harness.Presentation()), (_, _) => Task.FromResult(new SessionToolSet([tool])));
        await h.Start();
        var s = h.Session!;
        s.RaiseToolCall("d", "c", "external_action", "{}");
        await Eventually(() => s.Sent.Any(x => x.EventId == "c"));
        await h.Presenter.WaitUntilIdleAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(8)); await h.Presenter.WaitUntilIdleAsync();
        if (action == "deadline") h.Clock.Advance(TimeSpan.FromSeconds(10));
        else if (action == "button") await h.Presenter.NextAsync();
        else { s.Hear("no", 2000, 2100); await h.Presenter.WaitUntilIdleAsync(); h.Clock.Advance(TimeSpan.FromMilliseconds(701)); }
        await h.Presenter.WaitUntilIdleAsync();
        Assert.Equal(0, tool.Count);
        if (action == "no") Assert.Contains(h.Logs, x => x.Contains("declined"));
        if (action == "deadline") Assert.Contains(h.Logs, x => x.Contains("not confirmed"));
    }

    [Fact]
    public async Task Client_mode_disposes_and_does_not_offer_external_tools()
    {
        var disposable = new CountingSet();
        await using var h = new Harness((_, id, _) => Task.FromResult(Harness.Presentation()), (_, _) => Task.FromResult(new SessionToolSet([new ExternalTool()], disposable: disposable)), client: true);
        await h.Start();
        await Eventually(() => disposable.Count == 1);
        Assert.Contains(h.Logs, x => x.Contains("external tools need a delegation model"));
        Assert.Empty(h.Session!.Request!.HostedTools ?? []);
    }

    private static async Task Eventually(Func<bool> predicate)
    {
        for (var i = 0; i < 200; i++)
        {
            try { if (predicate()) return; }
            catch (InvalidOperationException) { /* The presenter is still adding frames; retry after it settles. */ }
            await Task.Delay(10);
        }
        Assert.True(predicate());
    }

    private sealed class OtherExternalTool : ITool
    {
        public string Name => "other_action";
        public string Description => "Other action";
        public JsonObject Parameters => new() { ["type"] = "object", ["properties"] = new JsonObject() };
        public IReadOnlyList<string> Tags => [];
        public bool Pinned => false;
        public bool RequiresConfirmation => true;
        public int Count;
        public Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct) { Interlocked.Increment(ref Count); return Task.FromResult(ToolResult.Success("done")); }
    }

    private sealed class CountingSet : IAsyncDisposable
    {
        public int Count;
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Count); return ValueTask.CompletedTask; }
    }

    private sealed class ExternalTool : ITool
    {
        public string Name => NameValue;
        public string NameValue = "external_action";
        public string SourceValue = "Fake";
        public string Description => "External action";
        public JsonObject Parameters => new() { ["type"] = "object", ["properties"] = new JsonObject() };
        public IReadOnlyList<string> Tags => [];
        public bool Pinned => false;
        public bool Ask = true;
        public bool RequiresConfirmation => Ask;
        public string Source => SourceValue;
        public TaskCompletionSource<ToolResult>? Gate;
        public int Count;
        public Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
        {
            Interlocked.Increment(ref Count);
            return Gate?.Task ?? Task.FromResult(ToolResult.Success("done"));
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public FakeTimeProvider Clock = new();
        public Presenter Presenter;
        public FakeSession? Session;
        public string? Owner;
        public ConcurrentQueue<string> Logs = new();
        public Harness(Func<string, string, CancellationToken, Task<LoadedPresentation>> load,
            Func<string, CancellationToken, Task<SessionToolSet>> source, TimeSpan? budget = null, int maxInline = 16, bool client = false)
        {
            Presenter = new Presenter((request, _) => Session = new FakeSession { Request = request, DelegationMode = client ? "client" : "responses" },
                load, new PresenterSettings(3000, "marin", 5000, maxInline), Clock, null, _ => true,
                (owner, ct) => { Owner = owner; return source(owner, ct); }, budget);
            Presenter.Log += l => Logs.Enqueue(l.Message);
        }
        public static LoadedPresentation Presentation() => new("p", new PresentationMeta("p", "Test", "deck", "showFn", null, null, 3000, 300), Slides, null);
        public Task<PresenterStartResult> Start() => Presenter.StartAsync("p", null, "owner");
        public ValueTask DisposeAsync() => Presenter.DisposeAsync();
    }
}
