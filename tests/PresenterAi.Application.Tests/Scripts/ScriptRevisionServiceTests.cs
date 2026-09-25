using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using PresenterAi.Application.Scripts;
using PresenterAi.Application.Scripts.Revisions;
using Xunit;

namespace PresenterAi.Application.Tests.Scripts;

/// <summary>
/// Plan 010 T6: the revision service over the in-memory store (gate between CAS and commit), a gated fake reviser and
/// a fake clock. The Postgres variants (two presentations concurrently, End between CAS and COMMIT on a real
/// transaction) are T11's <c>ScriptRevisionServicePostgresTests</c>.
/// </summary>
public sealed class ScriptRevisionServiceTests
{
    private const string Owner = "usr_owner";
    private const string Pid = "prs_sample";
    private const string Talk = "talk_1";

    [Fact]
    public async Task Edits_run_one_at_a_time_in_arrival_order()
    {
        await using var h = new Harness();
        h.Open();
        var gate = h.Reviser.HoldNextCall();

        var first = h.Edit(0, "first");
        await gate.Entered.Task.WaitAsync(Harness.Wait);
        var second = h.Edit(1, "second");
        var third = h.Edit(2, "third");
        await Task.Delay(50);

        Assert.Single(h.Reviser.Calls);
        Assert.Equal(ScriptEditStatus.Queued, h.Snapshot(second).Status);
        Assert.Equal(ScriptEditStatus.Queued, h.Snapshot(third).Status);
        gate.Release.SetResult();

        Assert.Equal(2, (await h.WaitTerminalAsync(first)).Version);
        Assert.Equal(3, (await h.WaitTerminalAsync(second)).Version);
        Assert.Equal(4, (await h.WaitTerminalAsync(third)).Version);
        Assert.Equal(["first", "second", "third"], h.Reviser.Calls.Select(c => c.Request.Feedback));
        var list = (await h.Store.ListAsync(Owner, Pid, 1, 10))!;
        Assert.Equal([4, 3, 2, 1], list.Items.Select(r => r.Number));
        Assert.Equal([[2], [1], [0]], list.Items.Take(3).Select(r => r.ChangedSlides.ToArray()));
        Assert.All(list.Items.Take(3), r => Assert.Equal(RevisionSources.LiveEdit, r.Source));
    }

    [Fact]
    public async Task Queued_edit_is_applied_on_the_newest_version()
    {
        await using var h = new Harness();
        h.Open();
        var gate = h.Reviser.HoldNextCall();
        var first = h.Edit(0, "one", baseVersion: 1);
        await gate.Entered.Task.WaitAsync(Harness.Wait);
        var second = h.Edit(0, "two", baseVersion: 1);
        gate.Release.SetResult();

        await h.WaitTerminalAsync(first);
        var applied = await h.WaitTerminalAsync(second);

        Assert.Equal(ScriptEditStatus.Applied, applied.Status);
        Assert.Equal(3, applied.Version);
        // Edit 2's reviser input already shows edit 1's text (applied on v2, not on the spoken base v1).
        Assert.EndsWith(" one", h.Reviser.Calls[1].Request.Targets[0].Narration, StringComparison.Ordinal);
        var head = (await h.Store.GetHeadAsync(Owner, Pid))!;
        Assert.EndsWith(" one two", head.Slides[0].Narration, StringComparison.Ordinal);
        Assert.Equal(2, (await h.Store.GetAsync(Owner, Pid, 3))!.Info.BaseVersion);
    }

    [Fact]
    public async Task Revert_during_processing_is_kept_and_the_edit_applies_on_top()
    {
        await using var h = new Harness();
        h.Open();
        var a = h.Edit(2, "slide three change");
        Assert.Equal(2, (await h.WaitTerminalAsync(a)).Version);

        var gate = h.Reviser.HoldNextCall();
        var b = h.Edit(1, "slide two change");
        await gate.Entered.Task.WaitAsync(Harness.Wait);
        var revert = await h.Service.RevertAsync(Owner, Pid, 1, Owner);

        var reverted = Assert.IsType<RevertResult.Reverted>(revert);
        Assert.Equal(3, reverted.Revision.Number);
        Assert.Equal(1, reverted.Revision.RevertedFrom);
        Assert.Equal(RevisionSources.Revert, reverted.Revision.Source);
        var pending = Assert.Single(reverted.PendingEdits);
        Assert.Equal((b, ScriptEditStatus.Processing), (pending.Id, pending.Status));
        Assert.Equal([1], pending.SlideIndexes);
        gate.Release.SetResult();

        var applied = await h.WaitTerminalAsync(b);
        Assert.Equal(4, applied.Version);
        var head = (await h.Store.GetHeadAsync(Owner, Pid))!;
        Assert.Equal(h.Original.Slides[2].Narration, head.Slides[2].Narration);
        Assert.EndsWith(" slide two change", head.Slides[1].Narration, StringComparison.Ordinal);
        // The target was unchanged by the revert, so the rewrite was reused: one reviser call per edit.
        Assert.Equal(2, h.Reviser.Calls.Count);
        var revertRow = (await h.Store.GetAsync(Owner, Pid, 3))!;
        Assert.Equal(RevisionSources.Revert, revertRow.Info.Source);
        Assert.Equal((await h.Store.GetAsync(Owner, Pid, 1))!.Script, revertRow.Script);
    }

    [Fact]
    public async Task Revert_to_older_narration_while_edit_targets_same_slide()
    {
        await using var h = new Harness();
        h.Open();
        var a = h.Edit(2, "first change");
        await h.WaitTerminalAsync(a);
        var gate = h.Reviser.HoldNextCall();
        var b = h.Edit(2, "second change");
        await gate.Entered.Task.WaitAsync(Harness.Wait);

        var reverted = Assert.IsType<RevertResult.Reverted>(await h.Service.RevertAsync(Owner, Pid, 1, Owner));

        Assert.Equal(3, reverted.Revision.Number);
        Assert.Equal([2], reverted.Revision.ChangedSlides);
        Assert.Equal(b, Assert.Single(reverted.PendingEdits).Id);
        var afterRevert = (await h.Store.GetHeadAsync(Owner, Pid))!;
        Assert.Equal(h.Original.Slides[2].Narration, afterRevert.Slides[2].Narration);
        gate.Release.SetResult();

        Assert.Equal(4, (await h.WaitTerminalAsync(b)).Version);
        // The rebuild re-called the reviser on the reverted (v1) text.
        Assert.Equal(3, h.Reviser.Calls.Count);
        Assert.Equal(h.Original.Slides[2].Narration, h.Reviser.Calls[2].Request.Targets[0].Narration);
        var head = (await h.Store.GetHeadAsync(Owner, Pid))!;
        Assert.Equal(h.Original.Slides[2].Narration + " second change", head.Slides[2].Narration);
        var list = (await h.Store.ListAsync(Owner, Pid, 1, 10))!;
        Assert.Equal(
            [RevisionSources.LiveEdit, RevisionSources.Revert, RevisionSources.LiveEdit, RevisionSources.Import],
            list.Items.Select(r => r.Source));
    }

    [Fact]
    public async Task Rebuild_reuses_rewrite_when_targets_unchanged()
    {
        await using var h = new Harness();
        h.Open();
        // Another writer (CLI import, another process) commits on slide 3 between the head read and the append.
        h.Scopes.OnCreate = n =>
        {
            if (n == 2)
            {
                h.AppendExternal(2, "Imported elsewhere.");
            }
        };

        var id = h.Edit(0, "edit");
        var outcome = await h.WaitTerminalAsync(id);

        Assert.Equal(ScriptEditStatus.Applied, outcome.Status);
        Assert.Equal(3, outcome.Version);
        Assert.Single(h.Reviser.Calls);
        var head = (await h.Store.GetHeadAsync(Owner, Pid))!;
        Assert.Equal("Imported elsewhere.", head.Slides[2].Narration);
        Assert.EndsWith(" edit", head.Slides[0].Narration, StringComparison.Ordinal);
        Assert.Equal(2, (await h.Store.GetAsync(Owner, Pid, 3))!.Info.BaseVersion);
        Assert.Equal(3, h.Service.GetReconciliationSnapshot(Pid, []).Head!.Version);
    }

    [Fact]
    public async Task Second_conflict_fails_with_conflict_and_writes_nothing()
    {
        await using var h = new Harness();
        h.Open();
        h.Scopes.OnCreate = n =>
        {
            if (n is 2 or 4)
            {
                h.AppendExternal(2, $"External {n}.");
            }
        };

        var id = h.Edit(0, "edit");
        var outcome = await h.WaitTerminalAsync(id);

        AssertOutcome(EditOutcome.Failed([0], ScriptEditErrors.Conflict), outcome);
        var list = (await h.Store.ListAsync(Owner, Pid, 1, 10))!;
        Assert.Equal(3, list.CurrentVersion);
        Assert.DoesNotContain(list.Items, r => r.Source == RevisionSources.LiveEdit);
        Assert.Equal(h.Original.Slides[0].Narration, (await h.Store.GetHeadAsync(Owner, Pid))!.Slides[0].Narration);
    }

    [Fact]
    public async Task Second_revert_conflict_returns_conflict()
    {
        await using var h = new Harness();
        h.Open();
        var a = h.Edit(0, "edit");
        await h.WaitTerminalAsync(a);
        var baseline = h.Scopes.Created;
        // Revert scopes: head, revision, append (conflict), head, append (conflict).
        h.Scopes.OnCreate = n =>
        {
            if (n == baseline + 3 || n == baseline + 5)
            {
                h.AppendExternal(2, $"External {n}.");
            }
        };

        var result = await h.Service.RevertAsync(Owner, Pid, 1, Owner);

        Assert.IsType<RevertResult.Conflict>(result);
        var list = (await h.Store.ListAsync(Owner, Pid, 1, 10))!;
        Assert.Equal(4, list.CurrentVersion);
        Assert.DoesNotContain(list.Items, r => r.Source == RevisionSources.Revert);
    }

    [Fact]
    public async Task Revert_reports_missing_presentation_and_revision()
    {
        await using var h = new Harness();

        Assert.IsType<RevertResult.PresentationNotFound>(await h.Service.RevertAsync("usr_other", Pid, 1, "usr_other"));
        Assert.IsType<RevertResult.RevisionNotFound>(await h.Service.RevertAsync(Owner, Pid, 7, Owner));
        Assert.Equal(1, (await h.Store.GetHeadAsync(Owner, Pid))!.Version);
    }

    public static TheoryData<string> BadOutputs => ["unchanged", "empty", "out_of_range", "not_requested", "marker", "failed"];

    [Theory]
    [MemberData(nameof(BadOutputs))]
    public async Task Invalid_or_zero_change_output_leaves_version_unchanged(string kind)
    {
        await using var h = new Harness();
        h.Open();
        h.Reviser.Respond = request => kind switch
        {
            "unchanged" => new ScriptRevisionResult.Ok([new RevisedSlide(2, request.Targets[0].Narration)], "No change"),
            "empty" => new ScriptRevisionResult.Ok([], "Nothing"),
            "out_of_range" => new ScriptRevisionResult.Ok([new RevisedSlide(9, "Text.")], "Out of range"),
            "not_requested" => new ScriptRevisionResult.Ok([new RevisedSlide(1, "Other slide.")], "Wrong slide"),
            "marker" => new ScriptRevisionResult.Ok([new RevisedSlide(2, "## Slide 9 — Injected\n\nText.")], "Marker"),
            _ => new ScriptRevisionResult.Failed(ScriptEditErrors.InvalidOutput)
        };

        var id = h.Edit(1, "change");
        var outcome = await h.WaitTerminalAsync(id);

        AssertOutcome(EditOutcome.Failed([1], ScriptEditErrors.InvalidOutput), outcome);
        Assert.Equal(1, (await h.Store.ListAsync(Owner, Pid, 1, 10))!.Total);
        Assert.Equal(1, (await h.Store.GetHeadAsync(Owner, Pid))!.Version);
    }

    [Fact]
    public async Task Upstream_failure_is_reported_as_is()
    {
        await using var h = new Harness();
        h.Open();
        h.Reviser.Respond = _ => new ScriptRevisionResult.Failed(ScriptEditErrors.Upstream);

        var outcome = await h.WaitTerminalAsync(h.Edit(1, "change"));

        Assert.Equal(ScriptEditErrors.Upstream, outcome.Error);
        Assert.Equal(1, (await h.Store.GetHeadAsync(Owner, Pid))!.Version);
    }

    [Fact]
    public async Task Timeout_after_60_seconds_fails_with_timeout()
    {
        await using var h = new Harness();
        h.Open();
        var gate = h.Reviser.HoldNextCall();
        var id = h.Edit(1, "slow");
        await gate.Entered.Task.WaitAsync(Harness.Wait);

        h.Time.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(50);
        Assert.Equal(ScriptEditStatus.Processing, h.Snapshot(id).Status);
        h.Time.Advance(TimeSpan.FromSeconds(1));

        AssertOutcome(EditOutcome.Failed([1], ScriptEditErrors.Timeout), await h.WaitTerminalAsync(id));
        Assert.Equal(1, (await h.Store.GetHeadAsync(Owner, Pid))!.Version);
    }

    [Fact]
    public async Task Commit_then_close_keeps_the_commit_and_close_waits()
    {
        await using var h = new Harness();
        var registration = h.Open();
        var commit = h.HoldNextCommit(honourToken: false);
        var id = h.Edit(1, "change");
        await commit.Entered.Task.WaitAsync(Harness.Wait);

        var close = h.Service.CloseTalkAsync(Talk);
        await Task.Delay(100);
        Assert.False(close.IsCompleted);
        Assert.False(registration.IsClosed);
        commit.Release.SetResult();
        await close.WaitAsync(Harness.Wait);

        Assert.True(registration.IsClosed);
        AssertOutcome(EditOutcome.Applied([1], 2, "change"), await h.WaitTerminalAsync(id));
        Assert.Equal(2, (await h.Store.GetHeadAsync(Owner, Pid))!.Version);
        Assert.Equal(2, (await h.Store.ListAsync(Owner, Pid, 1, 10))!.Total);
    }

    [Fact]
    public async Task Close_then_commit_makes_no_commit()
    {
        await using var h = new Harness();
        var registration = h.Open();
        // Stand in for a close that holds the lock: the worker reaches its commit phase while the close owns it.
        await registration.CommitLock.WaitAsync();
        var id = h.Edit(1, "change");
        await h.WaitForLogAsync("processing");
        await Task.Delay(100);
        Assert.Single(h.Reviser.Calls);
        Assert.Equal(1, (await h.Store.GetHeadAsync(Owner, Pid))!.Version);

        Assert.True(registration.MarkClosed());
        registration.CommitLock.Release();

        AssertOutcome(EditOutcome.Failed([1], ScriptEditErrors.Cancelled), await h.WaitTerminalAsync(id));
        // The commit phase saw Closed and never started a transaction: only the head-read scope was opened.
        Assert.Equal(1, h.Scopes.Created);
        Assert.Equal(1, (await h.Store.GetHeadAsync(Owner, Pid))!.Version);
        Assert.Equal(1, (await h.Store.ListAsync(Owner, Pid, 1, 10))!.Total);
    }

    [Fact]
    public async Task Close_during_revision_cancels_the_reviser_and_makes_no_commit()
    {
        await using var h = new Harness();
        var registration = h.Open();
        var gate = h.Reviser.HoldNextCall();
        var id = h.Edit(1, "change");
        await gate.Entered.Task.WaitAsync(Harness.Wait);

        await h.Service.CloseTalkAsync(Talk).WaitAsync(Harness.Wait);

        Assert.True(registration.IsClosed);
        Assert.True(h.Reviser.Calls[0].Token.IsCancellationRequested);
        await h.WaitForLogAsync($"edit: failed {id} (cancelled)");
        Assert.Equal(1, (await h.Store.GetHeadAsync(Owner, Pid))!.Version);
    }

    [Fact]
    public async Task Ticket_cancel_closes_the_talk_off_loop()
    {
        await using var h = new Harness();
        using var ticket = new CancellationTokenSource();
        var registration = h.Open(ticket: ticket.Token);
        var gate = h.Reviser.HoldNextCall();
        var id = h.Edit(1, "change");
        await gate.Entered.Task.WaitAsync(Harness.Wait);

        await ticket.CancelAsync();

        Assert.True(registration.IsClosed);
        Assert.True(h.Reviser.Calls[0].Token.IsCancellationRequested);
        await h.WaitForLogAsync($"edit: failed {id} (cancelled)");
        var late = h.Edit(1, "after close");
        Assert.Equal(ScriptEditStatus.Failed, h.Snapshot(late).Status);
        Assert.Equal(1, (await h.Store.GetHeadAsync(Owner, Pid))!.Version);
        Assert.Single(h.Reviser.Calls);
    }

    [Fact]
    public async Task Close_bound_elapses_on_a_stuck_commit_and_logs()
    {
        await using var h = new Harness();
        var registration = h.Open();
        var commit = h.HoldNextCommit(honourToken: true);
        var id = h.Edit(1, "change");
        await commit.Entered.Task.WaitAsync(Harness.Wait);

        var close = h.Service.CloseTalkAsync(Talk);
        await Task.Delay(50);
        Assert.False(close.IsCompleted);
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await close.WaitAsync(Harness.Wait);

        Assert.True(registration.IsClosed);
        Assert.Contains(h.Logger.Messages, m => m.Contains("close waited 5 s for an in-flight commit", StringComparison.Ordinal));
        // Cts cancelled the transaction before its commit: no row.
        await h.WaitForLogAsync($"edit: failed {id} (cancelled)");
        Assert.Equal(1, (await h.Store.GetHeadAsync(Owner, Pid))!.Version);
        Assert.Equal(1, (await h.Store.ListAsync(Owner, Pid, 1, 10))!.Total);
    }

    [Fact]
    public async Task Timed_out_close_does_not_release_an_unowned_permit()
    {
        await using var h = new Harness();
        using var ticket = new CancellationTokenSource();
        var registration = h.Open(ticket: ticket.Token);
        var commit = h.HoldNextCommit(honourToken: false);
        var stuck = h.Edit(1, "stuck");
        await commit.Entered.Task.WaitAsync(Harness.Wait);
        var queued = h.Edit(2, "queued behind");

        // End (loop) and the max-length ticket callback close concurrently; both time out on the stuck commit.
        var end = h.Service.CloseTalkAsync(Talk);
        await ticket.CancelAsync();
        await Task.Delay(50);
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await end.WaitAsync(Harness.Wait);
        Assert.True(registration.IsClosed);

        // The late transaction finishes and releases only its own permit.
        commit.Release.SetResult();
        await h.WaitForLogAsync($"edit: failed {stuck} (cancelled)");
        await h.WaitForLogAsync($"edit: failed {queued} (cancelled)");
        await h.WaitUntilAsync(() => h.Service.ActiveWorkerCount == 0);

        Assert.Equal(1, registration.CommitLock.CurrentCount);
        Assert.DoesNotContain(h.Logger.Entries, e => e.Level >= LogLevel.Error);
        Assert.Single(h.Reviser.Calls);
        Assert.Equal(1, (await h.Store.ListAsync(Owner, Pid, 1, 10))!.Total);

        // A next talk on the same presentation opens, edits and commits normally.
        h.Open("talk_2");
        var next = h.Edit(1, "next talk", talk: "talk_2");
        Assert.Equal(2, (await h.WaitTerminalAsync(next)).Version);
    }

    [Fact]
    public async Task Concurrent_close_callers_close_once()
    {
        await using var h = new Harness();
        using var ticket = new CancellationTokenSource();
        var registration = h.Open(ticket: ticket.Token);
        var cancellations = 0;
        registration.Cts.Token.Register(() => Interlocked.Increment(ref cancellations));

        var closes = Enumerable.Range(0, 4).Select(_ => Task.Run(() => h.Service.CloseTalkAsync(Talk))).ToList();
        closes.Add(Task.Run(() => ticket.CancelAsync()));
        await Task.WhenAll(closes).WaitAsync(Harness.Wait);
        await h.Service.CloseTalkAsync(Talk);

        Assert.True(registration.IsClosed);
        Assert.Equal(1, cancellations);
        Assert.Single(h.Logger.Messages, m => m.Contains($"edit: talk {Talk} closed", StringComparison.Ordinal));
        Assert.Equal(1, registration.CommitLock.CurrentCount);
    }

    [Fact]
    public async Task Snapshot_is_atomic_head_and_outcomes()
    {
        await using var h = new Harness();
        h.Open();
        var ids = new List<string>();
        var violations = new ConcurrentQueue<string>();
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var snapshot = h.Service.GetReconciliationSnapshot(Pid, Harness.AllIds);
                foreach (var (id, outcome) in snapshot.Outcomes)
                {
                    if (outcome.Status == ScriptEditStatus.Applied && (snapshot.Head is null || snapshot.Head.Version < outcome.Version))
                    {
                        violations.Enqueue($"{id} applied v{outcome.Version} with head v{snapshot.Head?.Version}");
                    }
                }
            }
        });

        for (var i = 0; i < 30; i++)
        {
            ids.Add(h.Edit(i % 3, $"change {i}"));
            if (ids.Count % 6 == 0)
            {
                await h.WaitTerminalAsync(ids[^1]);
            }
        }

        foreach (var id in ids)
        {
            Assert.Equal(ScriptEditStatus.Applied, (await h.WaitTerminalAsync(id)).Status);
        }

        await stop.CancelAsync();
        await reader;
        Assert.Empty(violations);
    }

    [Fact]
    public async Task Head_snapshot_is_monotonic_under_any_observe_order()
    {
        await using var h = new Harness();
        var signals = 0;
        h.Service.Changed += _ => Interlocked.Increment(ref signals);
        var slides = h.Original.Slides;

        foreach (var version in new[] { 3, 2, 5, 4, 5, 1 })
        {
            h.Service.Observe(new HeadSnapshot(Pid, version, slides));
        }

        Assert.Equal(5, h.Service.GetReconciliationSnapshot(Pid, []).Head!.Version);
        Assert.Equal(2, signals);
        Assert.Null(h.Service.GetReconciliationSnapshot("prs_unknown", []).Head);
    }

    [Fact]
    public async Task Terminal_outcome_is_written_once()
    {
        await using var h = new Harness();
        h.Open();
        // The reviser ignores cancellation and answers after the timeout already failed the edit.
        var gate = h.Reviser.HoldNextCall(ignoreCancellation: true);
        var id = h.Edit(1, "late");
        await gate.Entered.Task.WaitAsync(Harness.Wait);
        h.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(ScriptEditErrors.Timeout, (await h.WaitTerminalAsync(id)).Error);

        gate.Release.SetResult();
        await gate.Returned.Task.WaitAsync(Harness.Wait);
        await Task.Delay(100);

        AssertOutcome(EditOutcome.Failed([1], ScriptEditErrors.Timeout), h.Snapshot(id));
        Assert.Single(h.Seen(id), o => o.IsTerminal);
        Assert.Equal(1, (await h.Store.GetHeadAsync(Owner, Pid))!.Version);
    }

    [Fact]
    public async Task Changed_carries_no_payload_and_state_is_readable_after_it()
    {
        await using var h = new Harness();
        h.Open();
        var observed = new ConcurrentQueue<(string Signal, ReconciliationSnapshot Snapshot)>();
        h.Service.Changed += presentationId =>
            observed.Enqueue((presentationId, h.Service.GetReconciliationSnapshot(presentationId, Harness.AllIds)));

        var id = h.Edit(1, "change");
        await h.WaitTerminalAsync(id);

        Assert.All(observed, o => Assert.Equal(Pid, o.Signal));
        var applied = Assert.Single(observed, o => o.Snapshot.Outcomes.GetValueOrDefault(id)?.Status == ScriptEditStatus.Applied);
        Assert.Equal(2, applied.Snapshot.Head!.Version);
        Assert.EndsWith(" change", applied.Snapshot.Head.Slides[1].Narration, StringComparison.Ordinal);
        Assert.Contains(observed, o => o.Snapshot.Outcomes.GetValueOrDefault(id)?.Status == ScriptEditStatus.Processing);
    }

    [Fact]
    public async Task Talk_close_cancels_queued_items_without_a_model_call()
    {
        await using var h = new Harness();
        h.Open();
        var gate = h.Reviser.HoldNextCall();
        var first = h.Edit(0, "first");
        await gate.Entered.Task.WaitAsync(Harness.Wait);
        var second = h.Edit(1, "second");
        var third = h.Edit(2, "third");

        await h.Service.CloseTalkAsync(Talk).WaitAsync(Harness.Wait);

        foreach (var id in new[] { first, second, third })
        {
            await h.WaitForLogAsync($"edit: failed {id} (cancelled)");
        }

        Assert.Single(h.Reviser.Calls);
        await h.WaitUntilAsync(() => h.Service.ActiveWorkerCount == 0);
        Assert.Equal(1, (await h.Store.GetHeadAsync(Owner, Pid))!.Version);
    }

    [Fact]
    public async Task Queue_overflow_fails_queue_full()
    {
        await using var h = new Harness();
        h.Open();
        var gate = h.Reviser.HoldNextCall();
        var processing = h.Edit(0, "processing");
        await gate.Entered.Task.WaitAsync(Harness.Wait);

        var queued = Enumerable.Range(0, ScriptRevisionService.QueueCapacity).Select(i => h.Edit(1, $"q{i}")).ToArray();
        var overflow = h.Edit(1, "overflow");

        Assert.All(queued, id => Assert.Equal(ScriptEditStatus.Queued, h.Snapshot(id).Status));
        AssertOutcome(EditOutcome.Failed([1], ScriptEditErrors.QueueFull), h.Snapshot(overflow));
        gate.Release.SetResult();
        foreach (var id in queued)
        {
            Assert.Equal(ScriptEditStatus.Applied, (await h.WaitTerminalAsync(id)).Status);
        }

        Assert.Equal(1 + 1 + ScriptRevisionService.QueueCapacity, (await h.Store.GetHeadAsync(Owner, Pid))!.Version);
        await h.WaitTerminalAsync(processing);
    }

    [Fact]
    public async Task Overflow_then_close_in_the_same_talk_leaves_no_worker_running()
    {
        await using var h = new Harness();
        var registration = h.Open();
        var gate = h.Reviser.HoldNextCall();
        h.Edit(0, "processing");
        await gate.Entered.Task.WaitAsync(Harness.Wait);
        for (var i = 0; i <= ScriptRevisionService.QueueCapacity; i++)
        {
            h.Edit(1, $"q{i}");
        }

        await h.Service.CloseTalkAsync(Talk).WaitAsync(Harness.Wait);
        await h.WaitUntilAsync(() => h.Service.ActiveWorkerCount == 0);

        Assert.True(registration.IsClosed);
        Assert.Single(h.Reviser.Calls);
        Assert.Equal(1, (await h.Store.GetHeadAsync(Owner, Pid))!.Version);
    }

    [Fact]
    public async Task Dispose_cancels_workers_within_bound()
    {
        var h = new Harness();
        h.Open();
        var gate = h.Reviser.HoldNextCall();
        h.Edit(0, "hung");
        await gate.Entered.Task.WaitAsync(Harness.Wait);
        h.Edit(1, "queued");

        await h.Service.DisposeAsync().AsTask().WaitAsync(Harness.Wait);

        Assert.Equal(0, h.Service.ActiveWorkerCount);
        Assert.Single(h.Reviser.Calls);
        Assert.True(h.Reviser.Calls[0].Token.IsCancellationRequested);
        var after = h.Service.Enqueue(Talk, h.Request(1, "after dispose"));
        Assert.Equal(ScriptEditErrors.Cancelled, h.Snapshot(after).Error);
        Assert.Equal(1, (await h.Store.GetHeadAsync(Owner, Pid))!.Version);
    }

    [Fact]
    public async Task Dispose_returns_after_the_bound_when_a_commit_is_stuck()
    {
        var h = new Harness();
        h.Open();
        var commit = h.HoldNextCommit(honourToken: false);
        h.Edit(0, "stuck");
        await commit.Entered.Task.WaitAsync(Harness.Wait);

        var dispose = h.Service.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.False(dispose.IsCompleted);
        h.Time.Advance(ScriptRevisionService.DisposeBound);
        await dispose.WaitAsync(Harness.Wait);

        commit.Release.SetResult();
        await h.WaitUntilAsync(() => h.Service.ActiveWorkerCount == 0);
    }

    [Fact]
    public async Task Each_store_operation_uses_its_own_scope()
    {
        await using var h = new Harness();
        h.Open();

        var id = h.Edit(1, "change");
        await h.WaitTerminalAsync(id);

        // One scope for the head read, one for the append; none open while the reviser runs.
        Assert.Equal(2, h.Scopes.Created);
        Assert.Equal(0, Assert.Single(h.Reviser.Calls).OpenScopes);
        Assert.Equal(0, h.Scopes.Open);
        Assert.Equal(1, h.Scopes.MaxOpen);
        Assert.All(h.Scopes.Resolutions, count => Assert.Equal(1, count));

        Assert.IsType<RevertResult.Reverted>(await h.Service.RevertAsync(Owner, Pid, 1, Owner));
        // Revert: head, revision, append, stored row — each its own scope, never two open at once.
        Assert.Equal(6, h.Scopes.Created);
        Assert.Equal(0, h.Scopes.Open);
        Assert.Equal(1, h.Scopes.MaxOpen);
    }

    [Fact]
    public async Task Enqueue_for_an_unknown_or_foreign_talk_fails_not_presenting()
    {
        await using var h = new Harness();
        h.Open();

        var unknown = h.Service.Enqueue("talk_x", h.Request(0, "x"));
        var foreign = h.Service.Enqueue(Talk, h.Request(0, "x") with { OwnerId = "usr_other" });

        Assert.Equal(ScriptEditErrors.NotPresenting, h.Snapshot(unknown).Error);
        Assert.Equal(ScriptEditErrors.NotPresenting, h.Snapshot(foreign).Error);
        Assert.Empty(h.Reviser.Calls);
    }

    private static void AssertOutcome(EditOutcome expected, EditOutcome actual)
    {
        Assert.Equal(
            (expected.Status, string.Join(",", expected.SlideIndexes), expected.Version, expected.Summary, expected.Error),
            (actual.Status, string.Join(",", actual.SlideIndexes), actual.Version, actual.Summary, actual.Error));
    }

    private sealed class Harness : IAsyncDisposable
    {
        public static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

        public static readonly IReadOnlyCollection<string> AllIds = Enumerable.Range(1, 64).Select(i => $"edit_{i}").ToArray();

        private readonly ConcurrentQueue<CommitGate> _commitGates = new();
        private readonly ConcurrentDictionary<string, ConcurrentQueue<EditOutcome>> _seen = new(StringComparer.Ordinal);

        public Harness()
        {
            var markdown = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.md"));
            Original = ScriptParser.Parse(markdown, Pid);
            Store = new InMemoryPresentationRevisionStore(timeProvider: Time);
            Store.Seed(Owner, Pid, markdown);
            Store.BeforeCommitAsync = async (_, token) =>
            {
                if (_commitGates.TryDequeue(out var gate))
                {
                    gate.Entered.TrySetResult();
                    if (gate.HonourToken)
                    {
                        await gate.Release.Task.WaitAsync(token);
                    }
                    else
                    {
                        await gate.Release.Task;
                    }
                }
            };
            Scopes = new ScopeSpy(Store);
            Reviser = new GatedReviser(Scopes);
            Service = new ScriptRevisionService(
                Scopes,
                Reviser,
                new TrainingOptions { ReviserTimeoutSeconds = 60 },
                Time,
                Logger);
            Service.Changed += presentationId =>
            {
                foreach (var (id, outcome) in Service.GetReconciliationSnapshot(presentationId, AllIds).Outcomes)
                {
                    var seen = _seen.GetOrAdd(id, _ => new ConcurrentQueue<EditOutcome>());
                    if (!seen.TryPeekLast(out var last) || last != outcome)
                    {
                        seen.Enqueue(outcome);
                    }
                }
            };
        }

        public FakeTimeProvider Time { get; } = new();

        public InMemoryPresentationRevisionStore Store { get; }

        public PresentationScript Original { get; }

        public ScopeSpy Scopes { get; }

        public GatedReviser Reviser { get; }

        public CapturingLogger Logger { get; } = new();

        public ScriptRevisionService Service { get; }

        public TalkRegistration Open(string talkId = Talk, CancellationToken ticket = default) =>
            Service.OpenTalk(talkId, Owner, Pid, ticket);

        public ScriptEditRequest Request(int slideIndex, string feedback, int baseVersion = 1) =>
            new(Pid, Owner, [slideIndex], baseVersion, feedback, null, [new RecentTurn("user", "context only")]);

        public string Edit(int slideIndex, string feedback, int baseVersion = 1, string talk = Talk) =>
            Service.Enqueue(talk, Request(slideIndex, feedback, baseVersion));

        public EditOutcome Snapshot(string id) => Service.GetReconciliationSnapshot(Pid, [id]).Outcomes[id];

        /// <summary>Every distinct outcome of <paramref name="id"/> seen at a <c>Changed</c> signal, in order.</summary>
        public IReadOnlyList<EditOutcome> Seen(string id) =>
            _seen.TryGetValue(id, out var seen) ? seen.ToArray() : [];

        public async Task<EditOutcome> WaitTerminalAsync(string id)
        {
            EditOutcome? outcome = null;
            await WaitUntilAsync(() =>
            {
                var current = Service.GetReconciliationSnapshot(Pid, [id]).Outcomes.GetValueOrDefault(id);
                outcome = current is { IsTerminal: true } ? current : Seen(id).LastOrDefault(o => o.IsTerminal);
                return outcome is not null;
            });
            return outcome!;
        }

        public Task WaitForLogAsync(string fragment) =>
            WaitUntilAsync(() => Logger.Messages.Any(m => m.Contains(fragment, StringComparison.Ordinal)));

        public async Task WaitUntilAsync(Func<bool> condition)
        {
            var until = DateTime.UtcNow + Wait;
            while (!condition())
            {
                if (DateTime.UtcNow > until)
                {
                    throw new TimeoutException("Condition not reached. Log:\n" + string.Join('\n', Logger.Messages));
                }

                await Task.Delay(10);
            }
        }

        public CommitGate HoldNextCommit(bool honourToken)
        {
            var gate = new CommitGate(honourToken);
            _commitGates.Enqueue(gate);
            return gate;
        }

        /// <summary>A writer outside the service (CLI import, another process) commits on the current head.</summary>
        public void AppendExternal(int slideIndex, string narration)
        {
            var head = Store.GetHeadAsync(Owner, Pid).GetAwaiter().GetResult()!;
            var markdown = ScriptWriter.Format(head.Script with
            {
                Slides = head.Slides.Select(s => s.Index == slideIndex ? s with { Narration = narration } : s).ToArray()
            });
            var result = Store.TryAppendAsync(
                    Owner,
                    Pid,
                    head.Version,
                    new NewRevision(markdown, RevisionSources.Import, "External", head.Version, null, [slideIndex], null))
                .GetAwaiter()
                .GetResult();
            Assert.IsType<AppendResult.Applied>(result);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var gate in _commitGates)
            {
                gate.Release.TrySetResult();
            }

            Reviser.ReleaseAll();
            await Service.DisposeAsync();
        }
    }

    private sealed class CommitGate(bool honourToken)
    {
        public bool HonourToken { get; } = honourToken;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ReviserGate(bool ignoreCancellation)
    {
        public bool IgnoreCancellation { get; } = ignoreCancellation;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record ReviserCall(ScriptRevisionRequest Request, CancellationToken Token, int OpenScopes);

    private sealed class GatedReviser(ScopeSpy scopes) : IScriptReviser
    {
        private readonly ConcurrentQueue<ReviserGate> _gates = new();
        private readonly ConcurrentQueue<ReviserGate> _all = new();
        private readonly ConcurrentQueue<ReviserCall> _calls = new();

        public Func<ScriptRevisionRequest, ScriptRevisionResult>? Respond { get; set; }

        public IReadOnlyList<ReviserCall> Calls => _calls.ToArray();

        public ReviserGate HoldNextCall(bool ignoreCancellation = false)
        {
            var gate = new ReviserGate(ignoreCancellation);
            _gates.Enqueue(gate);
            _all.Enqueue(gate);
            return gate;
        }

        public void ReleaseAll()
        {
            foreach (var gate in _all)
            {
                gate.Release.TrySetResult();
            }
        }

        public async Task<ScriptRevisionResult> ReviseAsync(ScriptRevisionRequest request, CancellationToken cancellationToken)
        {
            _calls.Enqueue(new ReviserCall(request, cancellationToken, scopes.Open));
            if (_gates.TryDequeue(out var gate))
            {
                gate.Entered.TrySetResult();
                try
                {
                    if (gate.IgnoreCancellation)
                    {
                        await gate.Release.Task;
                    }
                    else
                    {
                        await gate.Release.Task.WaitAsync(cancellationToken);
                    }
                }
                finally
                {
                    gate.Returned.TrySetResult();
                }
            }

            return Respond?.Invoke(request) ?? new ScriptRevisionResult.Ok(
                request.Targets.Select(t => new RevisedSlide(t.Number, $"{t.Narration} {request.Feedback}")).ToArray(),
                request.Feedback);
        }
    }

    private sealed class ScopeSpy(IPresentationRevisionStore store) : IServiceScopeFactory
    {
        private int _created;
        private int _open;
        private int _maxOpen;
        private readonly ConcurrentQueue<int> _resolutions = new();

        public Action<int>? OnCreate { get; set; }

        public int Created => Volatile.Read(ref _created);

        public int Open => Volatile.Read(ref _open);

        public int MaxOpen => Volatile.Read(ref _maxOpen);

        /// <summary>Store resolutions per disposed scope.</summary>
        public IReadOnlyList<int> Resolutions => _resolutions.ToArray();

        public IServiceScope CreateScope()
        {
            var number = Interlocked.Increment(ref _created);
            var open = Interlocked.Increment(ref _open);
            int max;
            while (open > (max = Volatile.Read(ref _maxOpen)) && Interlocked.CompareExchange(ref _maxOpen, open, max) != max)
            {
            }

            OnCreate?.Invoke(number);
            return new Scope(this);
        }

        private sealed class Scope(ScopeSpy owner) : IServiceScope, IServiceProvider
        {
            private int _resolved;
            private int _disposed;

            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type serviceType)
            {
                if (serviceType != typeof(IPresentationRevisionStore))
                {
                    return null;
                }

                Interlocked.Increment(ref _resolved);
                return owner.StoreFor(this);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Decrement(ref owner._open);
                    owner._resolutions.Enqueue(_resolved);
                }
            }
        }

        private IPresentationRevisionStore StoreFor(Scope scope) => store;
    }

    private sealed class CapturingLogger : ILogger<ScriptRevisionService>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<string> Messages => _entries.Select(e => e.Message).ToArray();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception) + (exception is null ? string.Empty : " " + exception)));
    }
}

internal static class ConcurrentQueueExtensions
{
    public static bool TryPeekLast<T>(this ConcurrentQueue<T> queue, out T value)
    {
        var items = queue.ToArray();
        if (items.Length == 0)
        {
            value = default!;
            return false;
        }

        value = items[^1];
        return true;
    }
}
