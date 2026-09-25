using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Scripts;
using PresenterAi.Application.Scripts.Revisions;
using PresenterAi.TestSupport;
using Xunit;

namespace PresenterAi.Application.Tests.Presenting;

/// <summary>
/// Plan 010 T7: Trainer mode, the presenter-owned <c>revise_script</c> intent, the narration gate and the reconcile to the
/// revision service's state. Runs against <see cref="FakeScriptRevisionService"/> (the real service is lane B's T6): the
/// test plays the worker by setting outcomes and heads in one critical section and raising the payload-free signal.
/// </summary>
public sealed class PresenterTrainingTests
{
    private const string Owner = "owner";
    private const string Pid = "deck";
    private const string Question = "Shall I add that to the script?";

    private static readonly Slide[] BaseSlides =
    [
        new(0, 1, "One", "Slide one narration.", null),
        new(1, 2, "Two", "Slide two narration.", null),
        new(2, 3, "Three", "Slide three narration.", null),
        new(3, 4, "Four", "Slide four narration.", null)
    ];

    // ---- AC1 / AC5: tool, question, confirmation --------------------------------------------------------------------

    [Fact]
    public async Task Voice_feedback_asks_to_add_it_and_yes_sends_the_reviser_request()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        h.S.Hear("On this slide also mention the 2025 figures", 500, 900);
        h.S.ModelTranscript("Noted.", 1000, 1200);
        await h.Settle();

        await h.Ask("{\"feedback\":\"Also mention the 2025 figures.\"}");
        var output = JsonNode.Parse(h.S.Sent.Single(s => s.EventId == "c1").Content!)!;
        Assert.Equal("confirmation_required", output["status"]?.ToString());
        Assert.Equal(Question, output["question"]?.ToString());
        Assert.Empty(h.Service.Enqueued);

        await h.Answer("yes");
        var edit = Assert.Single(h.Service.Enqueued);
        Assert.Equal(Assert.Single(h.Service.Talks).Key, edit.TalkId);
        Assert.Equal(Pid, edit.Request.PresentationId);
        Assert.Equal(Owner, edit.Request.OwnerId);
        Assert.Equal([0], edit.Request.TargetSlideIndexes);
        Assert.Equal(5, edit.Request.BaseVersion);
        Assert.Equal("Also mention the 2025 figures.", edit.Request.Feedback);
        Assert.Null(edit.Request.Exchange);
        Assert.Contains(edit.Request.Recent, t => t.Role == "user" && t.Text.Contains("2025 figures", StringComparison.Ordinal));
        Assert.Contains(edit.Request.Recent, t => t.Role == "assistant" && t.Text == "Noted.");
        Assert.Contains(h.Edits, e => e.Id == edit.Id && e.Status == ScriptEditStatus.Queued);
        Assert.Contains(h.S.Sent, s => s.Content == PromptBuilder.ScriptEditPendingInstruction());
    }

    [Fact]
    public async Task Discovery_call_tool_speaks_the_same_question()
    {
        await using var h = new Harness(maxInline: 1);
        await h.Start();
        await h.TrainerOn();
        Assert.Contains(h.S.Request!.Tools!, t => t["name"]?.ToString() == "call_tool");

        await h.Ask("{\"name\":\"revise_script\",\"arguments\":{\"feedback\":\"Say it louder.\"}}", name: "call_tool");
        var output = JsonNode.Parse(h.S.Sent.Single(s => s.EventId == "c1").Content!)!;
        Assert.Equal(Question, output["question"]?.ToString());
        await h.Answer("yes");
        Assert.Equal("Say it louder.", Assert.Single(h.Service.Enqueued).Request.Feedback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Trainer_off_tool_call_returns_trainer_mode_off_and_creates_no_revision(bool viaCallTool)
    {
        await using var h = new Harness(maxInline: viaCallTool ? 1 : 16);
        await h.Start();
        await h.Ask(viaCallTool
            ? "{\"name\":\"revise_script\",\"arguments\":{\"feedback\":\"Change it.\"}}"
            : "{\"feedback\":\"Change it.\"}", name: viaCallTool ? "call_tool" : "revise_script");

        var output = JsonNode.Parse(h.S.Sent.Single(s => s.EventId == "c1").Content!)!;
        Assert.Contains(ScriptEditErrors.TrainerModeOff, output["message"]?.ToString());
        Assert.Null(output["status"]);
        await h.Answer("yes");
        Assert.Empty(h.Service.Enqueued);
        Assert.DoesNotContain(h.S.Sent, s => s.Content == PromptBuilder.ScriptEditPendingInstruction());
    }

    [Theory]
    [InlineData("no")]
    [InlineData(null)]
    public async Task No_or_ten_seconds_silence_answers_as_question_and_creates_no_revision(string? answer)
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        await h.Ask("{\"feedback\":\"What were the 2025 figures?\"}");
        if (answer is null)
        {
            h.Clock.Advance(TimeSpan.FromSeconds(8));
            await h.Settle();
            h.Clock.Advance(TimeSpan.FromSeconds(10));
            await h.Settle();
        }
        else
        {
            await h.Answer(answer);
        }

        Assert.Empty(h.Service.Enqueued);
        Assert.Single(h.S.Sent, s => s.Content == PromptBuilder.ScriptEditDeclinedInstruction());
        Assert.DoesNotContain(h.Edits, e => e.Status == ScriptEditStatus.Queued);
    }

    [Fact]
    public async Task Vietnamese_co_confirms_and_khong_declines_the_edit()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();

        // A question ending in the particle "không" is not an answer: the confirmation keeps waiting.
        await h.Ask("{\"feedback\":\"Thêm số liệu năm 2025.\"}");
        await h.Answer("Bạn có biết không?");
        Assert.Empty(h.Service.Enqueued);
        h.S.Hear("có", 3000, 3100);
        await h.Settle();
        h.Clock.Advance(TimeSpan.FromMilliseconds(701));
        await h.Settle();
        Assert.Single(h.Service.Enqueued);

        await h.Ask("{\"feedback\":\"Bỏ câu cuối.\"}", callId: "c2");
        await h.Answer("không");
        Assert.Single(h.Service.Enqueued);
        Assert.Single(h.S.Sent, s => s.Content == PromptBuilder.ScriptEditDeclinedInstruction());
    }

    // ---- Presenter-owned intent (D3 / D11) --------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task One_approval_enqueues_exactly_one_edit_with_the_captured_intent(bool viaCallTool)
    {
        await using var h = new Harness(maxInline: viaCallTool ? 1 : 16);
        await h.Start();
        await h.TrainerOn();
        Assert.True(await h.Presenter.GotoAsync(1));
        await h.Settle();
        await h.Ask(viaCallTool
            ? "{\"name\":\"revise_script\",\"arguments\":{\"feedback\":\"Shorter.\"}}"
            : "{\"feedback\":\"Shorter.\"}", name: viaCallTool ? "call_tool" : "revise_script");
        await h.Answer("yes");

        // The off-loop run only acknowledges; wait for it, then there is still exactly one enqueue.
        await Eventually(() => h.Logs.Any(l => l.Contains("revise_script ok", StringComparison.Ordinal)));
        await h.Settle();
        var edit = Assert.Single(h.Service.Enqueued);
        Assert.Equal(Assert.Single(h.Service.Talks).Key, edit.TalkId);
        Assert.Equal([1], edit.Request.TargetSlideIndexes);
        Assert.Equal(5, edit.Request.BaseVersion);
        Assert.DoesNotContain(h.S.Sent, s => s.Type == "commentary");
    }

    [Fact]
    public async Task Slide_numbers_of_the_call_are_the_captured_targets()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        await h.Ask("{\"feedback\":\"Fix the figures.\",\"slide_numbers\":[4,2,4]}");
        await h.Answer("yes");
        Assert.Equal([1, 3], Assert.Single(h.Service.Enqueued).Request.TargetSlideIndexes);
        // Neither target is the current slide, so narration is not held.
        Assert.DoesNotContain(h.S.Sent, s => s.EventId == "slide-1-hold");

        await h.Ask("{\"feedback\":\"Fix it.\",\"slide_numbers\":[5]}", callId: "c2");
        Assert.Contains("there are slides 1 to 4", h.S.Sent.Single(s => s.EventId == "c2").Content);
    }

    [Theory]
    [InlineData("{\"feedback\":\"x\",\"intent_id\":\"edit_9\"}", "revise_script")]
    [InlineData("{\"name\":\"revise_script\",\"arguments\":{\"feedback\":\"x\",\"talk_id\":\"talk_1\"}}", "call_tool")]
    public async Task Extra_argument_fields_are_rejected_by_the_validator(string arguments, string name)
    {
        await using var h = new Harness(maxInline: name == "call_tool" ? 1 : 16);
        await h.Start();
        await h.TrainerOn();
        h.S.RaiseToolCall("d", "c1", name, arguments);
        await Eventually(() => h.S.Sent.Any(s => s.EventId == "c1"));
        var output = JsonNode.Parse(h.S.Sent.Single(s => s.EventId == "c1").Content!)!;
        Assert.Contains("is not allowed", output["message"]?.ToString());
        Assert.Null(output["status"]);
        await h.Answer("yes");
        Assert.Empty(h.Service.Enqueued);
    }

    [Fact]
    public async Task Navigation_before_yes_cancels_the_confirmation_and_creates_no_edit()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        await h.Ask("{\"feedback\":\"Shorter.\"}");
        Assert.True(await h.Presenter.NextAsync());
        await h.Answer("yes");
        Assert.Empty(h.Service.Enqueued);
    }

    [Fact]
    public async Task Navigation_after_yes_keeps_the_original_target_and_replays_only_if_current()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        var id = await h.ConfirmEdit();
        Assert.Equal([0], h.Service.Enqueued.Single().Request.TargetSlideIndexes);

        Assert.True(await h.Presenter.NextAsync());
        await h.Settle();
        var sentBefore = h.S.Sent.Count;
        await h.Apply(id, 6, "Added 2025", (0, "Slide one with the 2025 figures."));
        // Slide 1 is not current: swapped, not replayed, and the current slide 2 is not re-presented.
        Assert.DoesNotContain(h.S.Sent.Skip(sentBefore), s => s.EventId?.Contains("part", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(h.S.Sent.Skip(sentBefore), s => s.EventId?.Contains("updated", StringComparison.Ordinal) == true);
        Assert.Equal(1, h.Presenter.Snapshot().SlideIndex);

        Assert.True(await h.Presenter.PrevAsync());
        await h.Settle();
        Assert.Contains("Slide one with the 2025 figures.", h.S.Sent.Last(s => s.EventId == "slide-1-part-1").Content);
    }

    [Fact]
    public async Task Approval_after_the_talk_ended_enqueues_nothing()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        await h.Ask("{\"feedback\":\"Shorter.\"}");
        Assert.True(await h.Presenter.EndAsync());
        await h.Settle();
        await h.Start();
        await h.TrainerOn();
        await h.Answer("yes");
        Assert.Empty(h.Service.Enqueued);
        Assert.Equal(2, h.Service.Talks.Count);
    }

    // ---- AC2: one narration gate ------------------------------------------------------------------------------------

    [Fact]
    public async Task Late_audio_while_held_arms_no_part_gap_or_advance()
    {
        await using var h = new Harness(slides: TwoPartFirstSlide(), chunkChars: 25);
        await h.Start();
        await h.TrainerOn();
        Assert.Contains(h.S.Sent, s => s.EventId == "slide-1-part-1");
        await h.TrainOn(0);

        h.S.Speak(startMs: 5000, endMs: 5100);
        await h.Settle();
        await h.Advance(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(h.S.Sent, s => s.EventId == "slide-1-part-2");
        Assert.DoesNotContain(h.S.Sent, s => s.EventId == "slide-2-part-1");
        Assert.DoesNotContain(1, h.SlideEvents);
    }

    [Fact]
    public async Task Timers_elapsing_while_held_do_not_progress()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        h.S.Speak(startMs: 100, endMs: 200);
        await h.Settle();
        await h.TrainOn(0);
        // A question hold opened and released by its 15 s timer must not resume or advance a held slide.
        h.S.Hear("what about Europe", 300, 400);
        await h.Settle();
        for (var i = 0; i < 8; i++) await h.Advance(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(h.S.Sent, s => s.EventId?.Contains("resume", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(h.S.Sent, s => s.EventId == "slide-2-part-1");
        Assert.Equal(0, h.Presenter.Snapshot().SlideIndex);
    }

    [Fact]
    public async Task Nudge_while_held_is_suppressed()
    {
        // Entering the hold clears the nudge timer and no held path re-arms it; OnNudge's own check is defence in depth.
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        await h.TrainOn(0);
        for (var i = 0; i < 4; i++) await h.Advance(TimeSpan.FromSeconds(15));
        Assert.DoesNotContain(h.S.Sent, s => s.EventId?.Contains("nudge", StringComparison.Ordinal) == true);
        Assert.Equal("presenting", h.Presenter.Snapshot().State);
    }

    [Fact]
    public async Task Pause_and_resume_while_held_send_no_resume_instruction()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        await h.TrainOn(0);
        Assert.True(await h.Presenter.PauseAsync());
        Assert.True(await h.Presenter.ResumeAsync());
        await h.Settle();
        Assert.Equal("presenting", h.Presenter.Snapshot().State);
        Assert.DoesNotContain(h.S.Sent, s => s.EventId?.StartsWith("resume-", StringComparison.Ordinal) == true);
        Assert.Single(h.S.Sent, s => s.EventId == "slide-1-part-1");
    }

    [Fact]
    public async Task Held_reconnect_is_presenting_and_completion_replays_without_a_second_resume()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        var id = await h.TrainOn(0);
        Assert.True(await h.Presenter.PauseAsync());
        await h.Advance(TimeSpan.FromSeconds(120));
        Assert.True(h.Presenter.Snapshot().Suspended);

        var slideEvents = h.SlideEvents.Count;
        Assert.True(await h.Presenter.ResumeAsync());
        await h.Settle();
        var second = h.Sessions[1];
        Assert.Equal("presenting", h.Presenter.Snapshot().State);
        Assert.Equal(slideEvents, h.SlideEvents.Count);
        Assert.DoesNotContain(second.Sent, s => s.EventId == "slide-1-hold");
        Assert.DoesNotContain(second.Sent, s => s.EventId?.StartsWith("resume-", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(second.Sent, s => s.EventId?.Contains("part", StringComparison.Ordinal) == true);

        // Idle guard runs again: activity is recorded, so a keep-alive keeps the talk up past the idle window.
        await h.Apply(id, 6, "Updated", (0, "Slide one, new text."));
        Assert.Contains("Slide one, new text.", second.Sent.Single(s => s.EventId == "slide-1-part-1").Content);
        Assert.DoesNotContain(second.Sent, s => s.EventId?.StartsWith("resume-", StringComparison.Ordinal) == true);
        Assert.Empty(h.Closed);
    }

    [Fact]
    public async Task Navigate_away_and_back_while_pending_re_holds_on_arrival()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        await h.TrainOn(0);
        Assert.Single(h.S.Sent, s => s.EventId == "slide-1-hold");
        Assert.True(await h.Presenter.NextAsync());
        Assert.Contains(h.S.Sent, s => s.EventId == "slide-2-part-1");
        Assert.True(await h.Presenter.PrevAsync());
        await h.Settle();
        Assert.Equal(2, h.S.Sent.Count(s => s.EventId == "slide-1-hold"));
        Assert.Single(h.S.Sent, s => s.EventId == "slide-1-part-1");
    }

    [Fact]
    public async Task Last_slide_held_does_not_start_wrap_up()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        Assert.True(await h.Presenter.GotoAsync(3));
        h.S.Speak(startMs: 100, endMs: 200);
        await h.Settle();
        await h.TrainOn(3);
        h.S.Speak(startMs: 300, endMs: 400);
        await h.Settle();
        await h.Advance(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(h.S.Sent, s => s.EventId == "wrap-up");

        // An explicit Next is navigation and may still finish the talk.
        Assert.True(await h.Presenter.NextAsync());
        Assert.Contains(h.S.Sent, s => s.EventId == "wrap-up");
    }

    [Fact]
    public async Task Entering_the_hold_flushes_and_interrupts()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        var flushes = h.Flushes;
        await h.TrainOn(0);
        Assert.Equal(flushes + 1, h.Flushes);
        var hold = h.S.Sent.Single(s => s.EventId == "slide-1-hold");
        Assert.StartsWith("Stop whatever you are saying now.", hold.Content);
    }

    [Fact]
    public async Task Max_length_still_ends_a_held_talk()
    {
        await using var h = new Harness(settings: new PresenterSettings(3000, "marin", 5000, 16, MaxTalkMinutes: 5));
        await h.Start();
        await h.TrainerOn();
        await h.TrainOn(0);
        // 5.5 min: a keep-alive due at the same instant as the deadline may re-arm the guard for the next tick.
        for (var i = 0; i < 11; i++) await h.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(EndReasons.MaxLength, Assert.Single(h.Closed).EndReason);
    }

    [Fact]
    public async Task Pending_edit_on_later_slide_lets_narration_continue_then_holds_on_arrival()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        var flushes = h.Flushes;
        await h.TrainOn(1);
        Assert.Equal(flushes, h.Flushes);
        Assert.DoesNotContain(h.S.Sent, s => s.EventId == "slide-1-hold");

        h.S.Speak(startMs: 100, endMs: 200);
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(3100));
        Assert.Equal(1, h.Presenter.Snapshot().SlideIndex);
        Assert.Contains(h.S.Sent, s => s.EventId == "slide-2-hold");
        Assert.DoesNotContain(h.S.Sent, s => s.EventId == "slide-2-part-1");
    }

    // ---- Reconcile (D4 / D10 / D14) ---------------------------------------------------------------------------------

    [Fact]
    public async Task Reversed_and_duplicated_signals_converge_to_the_head()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        Assert.True(await h.Presenter.GotoAsync(1));
        h.Service.RaiseChanged(Pid); // a signal before any commit changes nothing
        await h.Settle();
        var a = await h.TrainOn(1);
        var b = await h.TrainOn(3);

        // The "worker" commits A (v6, current slide 2) and B (v7, later slide 4); a revert then makes v8 (slide 2 again).
        // Signals carry nothing, so their order, duplication or loss before the last one cannot matter.
        var v6 = h.With(6, (1, "Two, version six."));
        h.Service.SetOutcome(a, EditOutcome.Applied([1], 6, "A"), v6);
        var v7 = h.With(7, (3, "Four, version seven."));
        h.Service.SetOutcome(b, EditOutcome.Applied([3], 7, "B"), v7);
        h.Service.Observe(h.With(8, (1, "Two, reverted.")));
        for (var i = 0; i < 4; i++) h.Service.RaiseChanged(Pid);
        await h.Settle();
        h.Service.RaiseChanged(Pid);
        h.Service.RaiseChanged(Pid);
        await h.Settle();

        Assert.Single(h.Edits, e => e.Id == a && e.Status == ScriptEditStatus.Applied);
        Assert.Single(h.Edits, e => e.Id == b && e.Status == ScriptEditStatus.Applied);
        var versions = h.Versions.Select(v => v.Version).ToArray();
        Assert.Equal(versions.OrderBy(v => v), versions);
        Assert.Equal(8, versions[^1]);
        Assert.Single(h.S.Sent, s => s.EventId == "slide-2-updated-v8");
        Assert.Contains("Two, reverted.", h.S.Sent.Last(s => s.EventId == "slide-2-part-1").Content);
        Assert.True(await h.Presenter.GotoAsync(3));
        Assert.Contains("Four, version seven.", h.S.Sent.Last(s => s.EventId == "slide-4-part-1").Content);
    }

    [Fact]
    public async Task Script_version_precedes_applied_and_parts_are_rebuilt_first()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        var id = await h.TrainOn(0);
        await h.Apply(id, 6, "New", (0, "Slide one, rebuilt."));

        var version = h.Timeline.FindIndex(t => t.Kind == "version:6");
        var applied = h.Timeline.FindIndex(t => t.Kind == $"edit:{id}:applied");
        Assert.InRange(version, 0, applied - 1);
        var replay = h.S.Sent.FindLastIndex(s => s.EventId == "slide-1-part-1");
        Assert.Contains("Slide one, rebuilt.", h.S.Sent[replay].Content);
        Assert.True(replay < h.Timeline[applied].SentCount, "the new narration is sent before applied is emitted");
    }

    [Fact]
    public async Task Commit_racing_the_reconcile_read_never_acknowledges_before_the_swap()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        var id = await h.TrainOn(0);
        h.Service.SetOutcome(id, EditOutcome.Processing([0]));
        var landed = 0;
        h.Service.BeforeSnapshot = _ =>
        {
            // The commit lands while the presenter is inside its read.
            if (Interlocked.Exchange(ref landed, 1) == 0)
                h.Service.SetOutcome(id, EditOutcome.Applied([0], 6, "Raced"), h.With(6, (0, "Slide one, raced.")));
        };
        h.Service.RaiseChanged(Pid);
        await h.Settle();
        h.Service.RaiseChanged(Pid);
        await h.Settle();

        var terminal = h.Edits.Where(e => e.Id == id && ScriptEditStatus.IsTerminal(e.Status)).ToArray();
        Assert.Single(terminal);
        var applied = h.Timeline.Single(t => t.Kind == $"edit:{id}:applied");
        var replay = h.S.Sent.FindLastIndex(s => s.EventId == "slide-1-part-1");
        Assert.Contains("Slide one, raced.", h.S.Sent[replay].Content);
        Assert.True(replay < applied.SentCount);
        Assert.DoesNotContain(h.Timeline.SkipWhile(t => t.Kind != $"edit:{id}:applied").Skip(1),
            t => t.Kind.StartsWith($"edit:{id}:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Applied_edit_replays_current_slide_with_new_narration()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        var id = await h.TrainOn(0);
        var flushes = h.Flushes;
        var sent = h.S.Sent.Count;
        await h.Apply(id, 6, "Added figures", (0, "Slide one with figures."));

        Assert.True(h.Flushes > flushes);
        var after = h.S.Sent.Skip(sent).ToArray();
        Assert.Contains(after, s => s.EventId == "slide-1-updated-v6");
        var replay = after.Single(s => s.EventId == "slide-1-part-1");
        Assert.Contains("Slide one with figures.", replay.Content);
        Assert.StartsWith("Stop whatever you are saying now.", replay.Content);
        var edit = h.Edits.Single(e => e.Status == ScriptEditStatus.Applied);
        Assert.Equal((6, "Added figures"), (edit.Version!.Value, edit.Summary));
        Assert.Equal(6, h.Presenter.CurrentScriptVersion()!.Version);
    }

    [Fact]
    public async Task Applied_while_paused_replays_on_resume()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        var id = await h.TrainOn(0);
        Assert.True(await h.Presenter.PauseAsync());
        var sent = h.S.Sent.Count;
        await h.Apply(id, 6, "Paused edit", (0, "Slide one while paused."));
        Assert.DoesNotContain(h.S.Sent.Skip(sent), s => s.EventId == "slide-1-part-1");

        Assert.True(await h.Presenter.ResumeAsync());
        await h.Settle();
        Assert.Contains("Slide one while paused.", h.S.Sent.Skip(sent).Single(s => s.EventId == "slide-1-part-1").Content);
        Assert.DoesNotContain(h.S.Sent.Skip(sent), s => s.EventId?.StartsWith("resume-", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Revert_mid_talk_replays_current_slide_if_changed()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        await h.Revert(6, (0, "Slide one as in version one."));
        Assert.Contains("Slide one as in version one.", h.S.Sent.Last(s => s.EventId == "slide-1-part-1").Content);
        Assert.Equal(2, h.S.Sent.Count(s => s.EventId == "slide-1-part-1"));
        Assert.Equal(6, h.Versions[^1].Version);
        Assert.Empty(h.Edits);
    }

    [Fact]
    public async Task Revert_for_unchanged_current_slide_does_not_replay()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        var sent = h.S.Sent.Count;
        await h.Revert(6, (2, "Slide three reverted."));
        Assert.DoesNotContain(h.S.Sent.Skip(sent), s => s.EventId?.Contains("part", StringComparison.Ordinal) == true);
        Assert.Equal(6, h.Versions[^1].Version);
        Assert.True(await h.Presenter.GotoAsync(2));
        Assert.Contains("Slide three reverted.", h.S.Sent.Last(s => s.EventId == "slide-3-part-1").Content);
    }

    [Fact]
    public async Task Failed_edit_while_paused_is_spoken_by_the_replay_at_resume()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        var id = await h.TrainOn(0);
        Assert.True(await h.Presenter.PauseAsync());
        h.Service.SetOutcome(id, EditOutcome.Failed([0], ScriptEditErrors.Timeout));
        h.Service.RaiseChanged(Pid);
        await h.Settle();

        // The resume replays the released slide with "stop whatever you are saying": it carries the notice, once.
        Assert.DoesNotContain(h.S.Sent, s => s.Content == PromptBuilder.ScriptEditFailedInstruction());
        Assert.True(await h.Presenter.ResumeAsync());
        await h.Settle();
        var replay = h.S.Sent.Last(s => s.EventId == "slide-1-part-1").Content!;
        Assert.StartsWith("Stop whatever you are saying now. " + PromptBuilder.ScriptEditFailedLead(), replay);
        Assert.Single(h.S.Sent, s => s.Content?.Contains(PromptBuilder.ScriptEditFailedLead(), StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Failed_edit_whose_replay_is_abandoned_by_navigation_leads_the_next_slide()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        var id = await h.TrainOn(0);
        Assert.True(await h.Presenter.PauseAsync());
        h.Service.SetOutcome(id, EditOutcome.Failed([0], ScriptEditErrors.Timeout));
        h.Service.RaiseChanged(Pid);
        await h.Settle();

        // Review r4: Next before Resume drops the replay; its notice must still be spoken, once, on the next slide.
        Assert.True(await h.Presenter.GotoAsync(1));
        await h.Settle();
        var next = h.S.Sent.Last(s => s.EventId == "slide-2-part-1").Content!;
        Assert.Contains(PromptBuilder.ScriptEditFailedLead(), next);
        Assert.Single(h.S.Sent, s => s.Content?.Contains(PromptBuilder.ScriptEditFailedLead(), StringComparison.Ordinal) == true);
        Assert.DoesNotContain(h.S.Sent, s => s.Content == PromptBuilder.ScriptEditFailedInstruction());

        Assert.True(await h.Presenter.GotoAsync(0));
        await h.Settle();
        Assert.DoesNotContain(PromptBuilder.ScriptEditFailedLead(), h.S.Sent.Last(s => s.EventId == "slide-1-part-1").Content!);
    }

    [Fact]
    public async Task Failed_edit_whose_replay_is_abandoned_by_the_wrap_up_leads_the_wrap_up()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        Assert.True(await h.Presenter.GotoAsync(3));
        await h.Settle();
        var id = await h.TrainOn(3);
        Assert.True(await h.Presenter.PauseAsync());
        h.Service.SetOutcome(id, EditOutcome.Failed([3], ScriptEditErrors.Timeout));
        h.Service.RaiseChanged(Pid);
        await h.Settle();

        Assert.True(await h.Presenter.NextAsync());
        await h.Settle();
        var wrapUp = Assert.Single(h.S.Sent, s => s.EventId == "wrap-up").Content!;
        Assert.Equal(PromptBuilder.ScriptEditFailedLead() + " " + PromptBuilder.WrapUpInstruction(), wrapUp);
        Assert.DoesNotContain(h.S.Sent, s => s.Content == PromptBuilder.ScriptEditFailedInstruction());
    }

    [Fact]
    public async Task Failed_edit_for_another_slide_speaks_failure_without_a_replay()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        var id = await h.TrainOn(2);
        var sent = h.S.Sent.Count;
        h.Service.SetOutcome(id, EditOutcome.Failed([2], ScriptEditErrors.Timeout));
        h.Service.RaiseChanged(Pid);
        await h.Settle();

        Assert.Single(h.S.Sent, s => s.Content == PromptBuilder.ScriptEditFailedInstruction());
        Assert.DoesNotContain(h.S.Sent.Skip(sent), s => s.EventId?.Contains("part", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(h.S.Sent, s => s.Content?.Contains(PromptBuilder.ScriptEditFailedLead(), StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Failed_edit_speaks_failure_once_releases_hold_and_keeps_text()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        var id = await h.TrainOn(0);
        h.Service.SetOutcome(id, EditOutcome.Failed([0], ScriptEditErrors.Timeout));
        h.Service.RaiseChanged(Pid);
        h.Service.RaiseChanged(Pid);
        await h.Settle();

        // T13 live run: a separate notice followed by the replay's "stop whatever you are saying" was never spoken, so
        // the replay that releases the hold carries the notice, once.
        Assert.DoesNotContain(h.S.Sent, s => s.Content == PromptBuilder.ScriptEditFailedInstruction());
        var replay = h.S.Sent.Last(s => s.EventId == "slide-1-part-1").Content!;
        Assert.StartsWith("Stop whatever you are saying now. " + PromptBuilder.ScriptEditFailedLead() + " Present slide 1", replay);
        Assert.Single(h.S.Sent, s => s.Content?.Contains(PromptBuilder.ScriptEditFailedLead(), StringComparison.Ordinal) == true);
        var failed = Assert.Single(h.Edits, e => ScriptEditStatus.IsTerminal(e.Status));
        Assert.Equal((ScriptEditStatus.Failed, ScriptEditErrors.Timeout), (failed.Status, failed.Error));
        Assert.Equal(2, h.S.Sent.Count(s => s.EventId == "slide-1-part-1"));
        Assert.Contains("Slide one narration.", h.S.Sent.Last(s => s.EventId == "slide-1-part-1").Content);
        Assert.DoesNotContain(h.S.Sent, s => s.EventId?.Contains("updated", StringComparison.Ordinal) == true);
        Assert.Equal(5, h.Presenter.CurrentScriptVersion()!.Version);

        // Released: timers progress again.
        h.S.Speak(startMs: 100, endMs: 200);
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(3100));
        Assert.Equal(1, h.Presenter.Snapshot().SlideIndex);
    }

    [Fact]
    public async Task Edit_on_earlier_slide_swaps_without_navigating()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        Assert.True(await h.Presenter.GotoAsync(2));
        var id = await h.TrainOn(0);
        var slides = h.SlideEvents.Count;
        await h.Apply(id, 6, "Earlier", (0, "Slide one, earlier edit."));
        Assert.Equal(slides, h.SlideEvents.Count);
        Assert.Equal(2, h.Presenter.Snapshot().SlideIndex);
        Assert.True(await h.Presenter.GotoAsync(0));
        Assert.Contains("Slide one, earlier edit.", h.S.Sent.Last(s => s.EventId == "slide-1-part-1").Content);
    }

    // ---- AC6: Train on this -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Train_on_turn_sends_the_exchange_for_the_given_slide()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        Assert.True(await h.Presenter.GotoAsync(2));
        await h.Settle();
        Assert.True(await h.Presenter.TrainOnTurnAsync(Owner, "What about 2025?", "Revenue grew 12%.", 1));
        await h.Settle();

        var edit = Assert.Single(h.Service.Enqueued);
        Assert.Equal([1], edit.Request.TargetSlideIndexes);
        Assert.Equal(new TrainingExchange("What about 2025?", "Revenue grew 12%."), edit.Request.Exchange);
        Assert.Equal(PromptBuilder.TrainOnTurnFeedback, edit.Request.Feedback);
        var flushes = h.Flushes;
        await h.Apply(edit.Id, 6, "Added growth", (1, "Slide two, revenue grew 12%."));
        Assert.Equal(flushes, h.Flushes); // not the current slide: no replay
        Assert.Equal(2, h.Presenter.Snapshot().SlideIndex);
    }

    [Fact]
    public async Task Train_on_turn_is_refused_when_trainer_mode_is_off_or_no_talk_runs()
    {
        await using var h = new Harness();
        Assert.False(await h.Presenter.TrainOnTurnAsync(Owner, "Q", "A", 0));
        Assert.Equal(ScriptEditErrors.NotPresenting, Assert.Single(h.Edits).Error);
        await h.Start();
        Assert.False(await h.Presenter.TrainOnTurnAsync(Owner, "Q", "A", 0));
        Assert.Equal(ScriptEditErrors.TrainerModeOff, h.Edits[^1].Error);
        await h.TrainerOn();
        Assert.False(await h.Presenter.TrainOnTurnAsync(Owner, "Q", "A", 4));
        Assert.False(await h.Presenter.TrainOnTurnAsync("intruder", "Q", "A", 0));
        Assert.Empty(h.Service.Enqueued);
    }

    // ---- Lifecycle --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Pending_edit_keeps_idle_guard_alive()
    {
        await using var h = new Harness(settings: new PresenterSettings(3000, "marin", 5000, 16, MaxTalkMinutes: 30));
        await h.Start();
        await h.TrainerOn();
        var id = await h.TrainOn(0);
        // Held: no audio, no nudge, no command for 7 min — past the 5 min idle window.
        for (var i = 0; i < 14; i++) await h.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(h.Closed);
        Assert.Equal("presenting", h.Presenter.Snapshot().State);
        Assert.NotNull(id);
    }

    [Fact]
    public async Task End_closes_the_talk_registration_before_the_upstream_and_drops_later_signals()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        var id = await h.TrainOn(0);
        var registration = h.Service.Talks.Single().Value;
        var sentAtClose = -1;
        registration.Cts.Token.Register(() => sentAtClose = h.S.Sent.Count(s => s.Type == "close"));
        // The wrap-up fallback ends the talk on the loop without cancelling the ticket, so only EndAsyncCore's
        // awaited close can close the registration before the upstream closes.
        Assert.True(await h.Presenter.GotoAsync(3));
        Assert.True(await h.Presenter.NextAsync());
        await h.Advance(TimeSpan.FromMilliseconds(Presenter.WrapUpFallbackMs));
        Assert.Equal(EndReasons.Completed, Assert.Single(h.Closed).EndReason);
        Assert.True(registration.IsClosed);
        Assert.Equal(0, sentAtClose);
        Assert.Contains(registration.TalkId, h.Service.ClosedTalkCalls);

        var frames = h.Edits.Count;
        h.Service.SetOutcome(id, EditOutcome.Applied([0], 6, "Late"), h.With(6, (0, "Late text.")));
        h.Service.RaiseChanged(Pid);
        await h.Settle();
        Assert.Equal(frames, h.Edits.Count);
        Assert.Null(h.Presenter.CurrentScriptVersion());
    }

    [Fact]
    public async Task Hung_reviser_is_cancelled_by_off_loop_end_while_the_loop_is_blocked()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        await h.TrainOn(0);
        var registration = h.Service.Talks.Single().Value;
        Assert.True(await h.Presenter.PauseAsync());
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.S.CloseGate = gate;
        h.Clock.Advance(TimeSpan.FromSeconds(120)); // the loop blocks in the pause suspension's close
        await Eventually(() => h.S.Sent.Any(s => s.Type == "close"));

        var end = h.Presenter.EndAsync();
        try
        {
            await Eventually(() => registration.IsClosed);
            Assert.True(registration.Cts.IsCancellationRequested);
            Assert.False(end.IsCompleted);
        }
        finally
        {
            gate.TrySetResult();
        }

        await end;
    }

    [Fact]
    public async Task Signals_from_a_previous_talk_are_ignored()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        var old = await h.TrainOn(0);
        Assert.True(await h.Presenter.EndAsync());
        await h.Settle();
        await h.Start();

        h.Service.SetOutcome(old, EditOutcome.Applied([0], 6, "Old talk"), h.With(6, (0, "From the old talk.")));
        h.Service.RaiseChanged(Pid);
        await h.Settle();
        Assert.DoesNotContain(h.Edits, e => e.Id == old && e.Status == ScriptEditStatus.Applied);
        // The head itself is state, not the old talk's acknowledgement: the running talk narrates the newest version.
        Assert.Equal(6, h.Versions[^1].Version);
    }

    [Fact]
    public async Task Start_failing_after_open_talk_closes_and_disposes_the_registration()
    {
        await using var h = new Harness(factory: index => new FakeSession { ThrowOnAppend = index == 0 });
        var result = await h.Presenter.StartAsync(Pid, null, Owner);
        await h.Settle();
        Assert.False(result.Started);
        var first = Assert.Single(h.Service.Talks).Value;
        Assert.True(first.IsClosed);
        Assert.Contains(first.TalkId, h.Service.ClosedTalkCalls);
        h.Presenter.AbortPendingStart();

        await h.Start();
        Assert.Equal(2, h.Service.Talks.Count);
        Assert.False(h.Service.Talks.Values.Single(t => t.TalkId != first.TalkId).IsClosed);
    }

    [Fact]
    public async Task Start_without_an_upstream_opens_no_registration()
    {
        await using var h = new Harness(factory: _ => new FakeSession { FailConnect = true });
        Assert.False((await h.Presenter.StartAsync(Pid, null, Owner)).Started);
        await h.Settle();
        Assert.Empty(h.Service.Talks);
        Assert.Empty(h.Versions);
    }

    [Fact]
    public async Task Unowned_trainer_toggle_is_refused()
    {
        await using var h = new Harness();
        await h.Start();
        Assert.False(await h.Presenter.SetTrainerModeAsync("intruder", true));
        Assert.DoesNotContain(h.Versions, v => v.TrainerMode);
        Assert.True(await h.Presenter.SetTrainerModeAsync(Owner, true));
        Assert.True(h.Versions[^1].TrainerMode);
    }

    [Fact]
    public async Task Idle_toggle_applies_only_to_the_same_owners_next_start()
    {
        await using var h = new Harness();
        Assert.True(await h.Presenter.SetTrainerModeAsync("someone-else", true));
        await h.Start();
        Assert.False(h.Versions[^1].TrainerMode);
        Assert.True(await h.Presenter.EndAsync());
        await h.Settle();

        Assert.True(await h.Presenter.SetTrainerModeAsync(Owner, true));
        await h.Start();
        Assert.True(h.Versions[^1].TrainerMode);
        Assert.True(await h.Presenter.EndAsync());
        await h.Settle();

        await h.Start();
        Assert.False(h.Versions[^1].TrainerMode);
    }

    [Fact]
    public async Task Trainer_mode_needs_a_reasoning_route()
    {
        await using var h = new Harness();
        h.Service.IsAvailable = false;
        await h.Start();
        Assert.False(await h.Presenter.SetTrainerModeAsync(Owner, true));
        Assert.Equal((false, false), (h.Versions[^1].TrainerMode, h.Versions[^1].TrainerAvailable));
    }

    // ---- Every Trainer mode change reaches the client (the idle toggle used to be stored silently) ---------------------

    [Fact]
    public async Task Idle_toggle_is_published_and_the_reset_at_end_is_published()
    {
        await using var h = new Harness();
        Assert.Equal(new PresenterTrainerState(null, false, true, true), h.Presenter.CurrentTrainerState());

        Assert.True(await h.Presenter.SetTrainerModeAsync(Owner, true));
        Assert.Equal(new PresenterTrainerState(Owner, true, true, true), h.TrainerStates[^1]);
        Assert.Equal(h.TrainerStates[^1], h.Presenter.CurrentTrainerState());

        await h.Start();
        Assert.True(h.Versions[^1].TrainerMode);
        Assert.Equal(new PresenterTrainerState(Owner, true, true, true), h.Presenter.CurrentTrainerState());

        Assert.True(await h.Presenter.EndAsync());
        await h.Settle();
        Assert.Equal(new PresenterTrainerState(null, false, true, true), h.TrainerStates[^1]);
        Assert.Equal(h.TrainerStates[^1], h.Presenter.CurrentTrainerState());

        Assert.True(await h.Presenter.SetTrainerModeAsync(Owner, true));
        Assert.True(await h.Presenter.SetTrainerModeAsync(Owner, false));
        Assert.Equal(new PresenterTrainerState(Owner, false, true, true), h.TrainerStates[^1]);
    }

    [Fact]
    public async Task Running_toggle_and_refusal_are_published()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        Assert.Equal(new PresenterTrainerState(Owner, true, true, true), h.TrainerStates[^1]);

        var count = h.TrainerStates.Count;
        Assert.False(await h.Presenter.SetTrainerModeAsync("intruder", false));
        Assert.Equal(count + 1, h.TrainerStates.Count);
        Assert.Equal(new PresenterTrainerState(Owner, true, true, true), h.TrainerStates[^1]);

        Assert.True(await h.Presenter.SetTrainerModeAsync(Owner, false));
        Assert.Equal(new PresenterTrainerState(Owner, false, true, true), h.TrainerStates[^1]);
    }

    [Fact]
    public async Task Idle_request_without_a_reasoning_route_is_published_as_off()
    {
        await using var h = new Harness();
        h.Service.IsAvailable = false;
        Assert.True(await h.Presenter.SetTrainerModeAsync(Owner, true));
        Assert.Equal(new PresenterTrainerState(Owner, false, false, true), h.TrainerStates[^1]);
    }

    [Fact]
    public async Task Upstream_loss_publishes_trainer_mode_off()
    {
        await using var h = new Harness(factory: index => new FakeSession { FailConnect = index > 0 });
        await h.Start();
        await h.TrainerOn();
        h.S.Drop();
        await Eventually(() => h.Presenter.Snapshot().State == "idle");
        await h.Settle();
        Assert.Equal(new PresenterTrainerState(null, false, true, true), h.TrainerStates[^1]);
    }

    // ---- A3: voice availability follows the connected session -------------------------------------------------------

    [Fact]
    public async Task Client_mode_connection_reports_voice_training_unavailable_and_transcript_still_works()
    {
        // The primary fails; the fallback route has no delegation model, so the talk runs in client mode.
        await using var h = new Harness(
            factory: index => new FakeSession { FailConnect = index == 0, DelegationMode = index == 0 ? "responses" : "client" },
            managed: attempt => attempt == 0);
        await h.Start();
        Assert.True(await h.Presenter.SetTrainerModeAsync(Owner, true));
        var version = h.Versions[^1];
        Assert.Equal((true, true, false), (version.TrainerMode, version.TrainerAvailable, version.VoiceTraining));
        Assert.True(await h.Presenter.TrainOnTurnAsync(Owner, "Q?", "A.", 0));
        Assert.Single(h.Service.Enqueued);
    }

    [Fact]
    public async Task Reconnect_to_a_different_delegation_mode_updates_voice_availability()
    {
        await using var h = new Harness(factory: index => new FakeSession { DelegationMode = index == 0 ? "responses" : "client" });
        await h.Start();
        await h.TrainerOn();
        Assert.True(h.Versions[^1].VoiceTraining);
        Assert.True(await h.Presenter.PauseAsync());
        await h.Advance(TimeSpan.FromSeconds(120));
        Assert.True(await h.Presenter.ResumeAsync());
        await h.Settle();
        Assert.False(h.Versions[^1].VoiceTraining);
        Assert.True(h.Versions[^1].TrainerMode);
    }

    // ---- Support ----------------------------------------------------------------------------------------------------

    private static Slide[] TwoPartFirstSlide() =>
    [
        new(0, 1, "One", "First part sentence. Second part sentence.", null),
        .. BaseSlides.Skip(1)
    ];

    private static async Task Eventually(Func<bool> predicate)
    {
        for (var i = 0; i < 300; i++)
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

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(
            Func<int, FakeSession>? factory = null,
            PresenterSettings? settings = null,
            IReadOnlyList<Slide>? slides = null,
            Func<int, bool>? managed = null,
            int maxInline = 16,
            int chunkChars = 1400)
        {
            Presenter = new Presenter(
                (request, _) =>
                {
                    var session = factory?.Invoke(Sessions.Count) ?? new FakeSession();
                    session.Request = request;
                    Sessions.Add(session);
                    return session;
                },
                (_, id, _) => Task.FromResult(new LoadedPresentation(id,
                    new PresentationMeta(id, "Title", "deck", "show", null, null, 3000, chunkChars), slides ?? BaseSlides, null, 5)),
                settings ?? new PresenterSettings(3000, "marin", 5000, maxInline),
                Clock,
                null,
                managed ?? (_ => true),
                null,
                null,
                Service);
            Presenter.Log += log => Logs.Enqueue(log.Message);
            Presenter.Closed += closed => Closed.Add(closed);
            Presenter.Flush += () => { Flushes++; Mark("flush"); };
            Presenter.Slide += index => { SlideEvents.Add(index); Mark($"slide:{index}"); };
            Presenter.ScriptEdit += edit => { Edits.Add(edit); Mark($"edit:{edit.Id}:{edit.Status}"); };
            Presenter.ScriptVersion += version => { Versions.Add(version); Mark($"version:{version.Version}"); };
            Presenter.TrainerState += state => TrainerStates.Add(state);
        }

        public FakeTimeProvider Clock { get; } = new();
        public FakeScriptRevisionService Service { get; } = new();
        public Presenter Presenter { get; }
        public List<FakeSession> Sessions { get; } = [];
        public ConcurrentQueue<string> Logs { get; } = new();
        public List<PresenterClosed> Closed { get; } = [];
        public List<PresenterScriptEdit> Edits { get; } = [];
        public List<PresenterScriptVersion> Versions { get; } = [];
        public List<PresenterTrainerState> TrainerStates { get; } = [];
        public List<int> SlideEvents { get; } = [];
        public List<(string Kind, int SentCount)> Timeline { get; } = [];
        public int Flushes { get; private set; }
        public FakeSession S => Sessions[^1];

        public Task Settle() => Presenter.WaitUntilIdleAsync();

        public async Task Start(string owner = Owner)
        {
            Assert.True((await Presenter.StartAsync(Pid, null, owner)).Started);
            await Settle();
        }

        public async Task TrainerOn(string owner = Owner)
        {
            Assert.True(await Presenter.SetTrainerModeAsync(owner, true));
            await Settle();
        }

        public async Task Advance(TimeSpan by)
        {
            Clock.Advance(by);
            await Settle();
        }

        public async Task Ask(string arguments, string callId = "c1", string name = "revise_script")
        {
            S.RaiseToolCall("d", callId, name, arguments);
            await Eventually(() => S.Sent.Any(s => s.EventId == callId));
            await Settle();
        }

        /// <summary>The confirmation question is voiced (8 s), then the speaker answers and the utterance completes.</summary>
        public async Task Answer(string words)
        {
            await Advance(TimeSpan.FromSeconds(8));
            S.Hear(words, 2000, 2100);
            await Settle();
            await Advance(TimeSpan.FromMilliseconds(701));
        }

        public async Task<string> ConfirmEdit(string feedback = "Also mention the 2025 figures.")
        {
            var before = Service.Enqueued.Count;
            await Ask($"{{\"feedback\":\"{feedback}\"}}", $"c{before + 1}");
            await Answer("yes");
            Assert.Equal(before + 1, Service.Enqueued.Count);
            return Service.Enqueued[^1].Id;
        }

        public async Task<string> TrainOn(int slideIndex)
        {
            Assert.True(await Presenter.TrainOnTurnAsync(Owner, "What about it?", "It is like this.", slideIndex));
            await Settle();
            return Service.Enqueued[^1].Id;
        }

        public HeadSnapshot With(int version, params (int Index, string Narration)[] changes)
        {
            var slides = Service.Head(Pid)!.Slides.ToArray();
            foreach (var (index, narration) in changes) slides[index] = slides[index] with { Narration = narration };
            return new HeadSnapshot(Pid, version, slides);
        }

        public async Task Apply(string editId, int version, string summary, params (int Index, string Narration)[] changes)
        {
            var targets = Service.Enqueued.Single(e => e.Id == editId).Request.TargetSlideIndexes;
            Assert.True(Service.SetOutcome(editId, EditOutcome.Applied(targets, version, summary), With(version, changes)));
            Service.RaiseChanged(Pid);
            await Settle();
        }

        public async Task Revert(int version, params (int Index, string Narration)[] changes)
        {
            Service.Observe(With(version, changes));
            Service.RaiseChanged(Pid);
            await Settle();
        }

        public ValueTask DisposeAsync() => Presenter.DisposeAsync();

        private void Mark(string kind) => Timeline.Add((kind, Sessions.Count == 0 ? 0 : S.Sent.Count));
    }
}
