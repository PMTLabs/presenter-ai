using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Scripts;
using PresenterAi.Application.Tools;
using PresenterAi.Application.Tools.External;
using Xunit;

namespace PresenterAi.Application.Tests.Presenting;

public sealed class PresenterLifetimeTests
{
    [Fact]
    public async Task Handler_exception_closes_the_upstream_and_the_next_start_works()
    {
        await using var h = Create();
        var throwOnce = true;
        h.Presenter.Audio += _ =>
        {
            if (throwOnce) { throwOnce = false; throw new InvalidOperationException("handler"); }
        };
        await h.Start();
        h.Sessions[0].Speak();
        await h.Settle();
        Assert.Equal(EndReasons.Error, Assert.Single(h.Closed).EndReason);
        Assert.Equal("InvalidOperationException", h.Closed[0].Reason);
        Assert.Equal(1, h.Sessions[0].DisposeCount);
        Assert.Equal("idle", h.Presenter.Snapshot().State);
        Assert.True((await h.Presenter.StartAsync("deck", null, "owner")).Started);
        Assert.Equal(2, h.Sessions.Count);
    }

    [Fact]
    public async Task Throw_after_session_assignment_disposes_it_and_returns_idle()
    {
        await using var h = Create(index => new FakeSession { ThrowOnAppend = index == 0 });
        Assert.False((await h.Presenter.StartAsync("deck", null, "owner")).Started);
        Assert.Equal("idle", h.Presenter.Snapshot().State);
        Assert.Equal(1, Assert.Single(h.Sessions).DisposeCount);
        Assert.Equal(EndReasons.Error, Assert.Single(h.Closed).EndReason);
    }

    [Fact]
    public async Task Dispose_during_a_hanging_connect_disposes_the_candidate_without_trying_the_fallback()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var h = Create(_ => new FakeSession { ConnectGate = gate });
        var start = h.Presenter.StartAsync("deck", null, "owner");
        await h.CandidateCreated();
        await h.Presenter.DisposeAsync();
        Assert.False((await start).Started);
        Assert.Equal(1, Assert.Single(h.Sessions).DisposeCount);
        Assert.Equal(EndReasons.Shutdown, Assert.Single(h.Closed).EndReason);
    }

    [Fact]
    public async Task Abort_pending_start_cancels_the_connect()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = Create(_ => new FakeSession { ConnectGate = gate });
        var start = h.Presenter.StartAsync("deck", null, "owner");
        await h.CandidateCreated();
        h.Presenter.AbortPendingStart();
        Assert.False((await start).Started);
        Assert.Equal(1, Assert.Single(h.Sessions).DisposeCount);
        Assert.Equal("idle", h.Presenter.Snapshot().State);
    }

    [Fact]
    public async Task Abort_pending_start_rejects_a_queued_start_without_creating_an_upstream()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = 0;
        await using var presenter = new Presenter((_, _) => { created++; return new FakeSession(); },
            async (_, id, _) =>
            {
                entered.TrySetResult();
                await gate.Task;
                return new LoadedPresentation(id,
                    new PresentationMeta(id, "Title", "deck", "show", null, null, null),
                    [new Slide(0, 1, "One", "Narration.", null)], null);
            }, new PresenterSettings(MaxTalkMinutes: 5), new FakeTimeProvider());
        var first = presenter.StartAsync("deck", null, "owner");
        await entered.Task;
        var queued = presenter.StartAsync("deck", null, "owner");
        presenter.AbortPendingStart();
        gate.TrySetResult();
        Assert.False((await first).Started);
        Assert.False((await queued).Started);
        Assert.Equal(0, created);
    }

    [Fact]
    public async Task Shutdown_with_a_queued_start_does_not_connect_after_the_loop_unblocks()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = 0;
        var presenter = new Presenter((_, _) => { created++; return new FakeSession(); },
            async (_, id, _) =>
            {
                entered.TrySetResult();
                await gate.Task;
                return new LoadedPresentation(id,
                    new PresentationMeta(id, "Title", "deck", "show", null, null, null),
                    [new Slide(0, 1, "One", "Narration.", null)], null);
            }, new PresenterSettings(MaxTalkMinutes: 5), new FakeTimeProvider());
        var first = presenter.StartAsync("deck", null, "owner");
        await entered.Task;
        var queued = presenter.StartAsync("deck", null, "owner");
        var shutdown = presenter.DisposeAsync();
        gate.TrySetResult();
        await shutdown;
        Assert.False((await first).Started);
        Assert.False((await queued).Started);
        Assert.Equal(0, created);
    }

    [Fact]
    public async Task Abort_pending_start_cancels_presentation_load()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = 0;
        await using var presenter = new Presenter((_, _) => { created++; return new FakeSession(); },
            async (_, _, token) =>
            {
                entered.SetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("load should not finish");
            }, new PresenterSettings(MaxTalkMinutes: 5), new FakeTimeProvider());
        var start = presenter.StartAsync("deck", null, "owner");
        await entered.Task;
        presenter.AbortPendingStart();
        Assert.False((await start).Started);
        Assert.Equal(0, created);
        Assert.Equal("idle", presenter.Snapshot().State);
    }

    [Fact]
    public async Task End_during_a_hanging_start_connect_cancels_it()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = Create(_ => new FakeSession { ConnectGate = gate });
        var start = h.Presenter.StartAsync("deck", null, "owner");
        await h.CandidateCreated();
        var end = h.Presenter.EndAsync(EndReasons.User);
        Assert.False((await start).Started);
        await end;
        Assert.Equal(1, Assert.Single(h.Sessions).DisposeCount);
        Assert.Equal(EndReasons.User, Assert.Single(h.Closed).EndReason);
    }

    [Fact]
    public async Task End_cancels_an_in_flight_tool_call()
    {
        var tool = new CancellableTool(false);
        await using var h = Create(tool: tool);
        await h.Start();
        h.Sessions[0].RaiseToolCall("d", "c", tool.Name, "{}");
        await tool.Started.Task;
        await h.Presenter.EndAsync();
        await tool.Cancelled.Task;
        await h.Settle();
        Assert.Equal(EndReasons.User, Assert.Single(h.Closed).EndReason);
        Assert.DoesNotContain(h.Sessions[0].Sent, s => s.Type == "tool_output" && s.EventId == "c");
    }

    [Fact]
    public async Task End_cancels_an_approved_tool_call()
    {
        var tool = new CancellableTool(true);
        await using var h = Create(tool: tool);
        await h.Start();
        h.Sessions[0].RaiseToolCall("d", "c", tool.Name, "{}");
        await h.Settle();
        h.Clock.Advance(TimeSpan.FromSeconds(8));
        await h.Settle();
        h.Sessions[0].Hear("yes", 2000, 2100);
        await h.Settle();
        h.Clock.Advance(TimeSpan.FromMilliseconds(701));
        await h.Settle();
        await tool.Started.Task;
        await h.Presenter.EndAsync();
        await tool.Cancelled.Task;
        await h.Settle();
        Assert.Equal(EndReasons.User, Assert.Single(h.Closed).EndReason);
    }

    [Fact]
    public async Task End_while_a_start_is_queued_behind_a_busy_idle_loop_rejects_it_without_an_upstream()
    {
        var h = CreateStartRace();
        await using var presenter = h.Presenter;
        var mute = h.BlockNextState("idle", () => presenter.MuteAsync());
        await h.Blocked.Task;
        var start = presenter.StartAsync("deck", null, "owner");
        var end = presenter.EndAsync(EndReasons.User);
        h.Release.SetResult();
        Assert.False((await start).Started);
        await mute;
        await end;
        await presenter.WaitUntilIdleAsync();
        Assert.Equal(0, h.Created);
        Assert.Empty(h.Closed);
        Assert.Equal("idle", presenter.Snapshot().State);
    }

    [Fact]
    public async Task End_while_a_start_is_queued_behind_a_starting_talk_rejects_it_and_ends_the_talk_as_user()
    {
        var loaderGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var h = CreateStartRace(async _ => await loaderGate.Task); // ignores cancellation
        await using var presenter = h.Presenter;
        var first = presenter.StartAsync("deck", null, "owner");
        await h.LoaderEntered.Task;
        var queued = presenter.StartAsync("deck", null, "owner");
        var end = presenter.EndAsync(EndReasons.User);
        loaderGate.SetResult();
        Assert.False((await first).Started);
        Assert.False((await queued).Started);
        await end;
        await presenter.WaitUntilIdleAsync();
        Assert.Equal(0, h.Created);
        Assert.Equal(EndReasons.User, Assert.Single(h.Closed).EndReason);
    }

    [Fact]
    public async Task End_while_a_start_is_being_accepted_cancels_the_load_and_a_later_cap_does_not_relabel_it()
    {
        var h = CreateStartRace(token => Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token));
        await using var presenter = h.Presenter;
        // Holds the loop inside acceptance: the snapshot already says connecting, the load has not begun.
        var start = h.BlockNextState("connecting", () => presenter.StartAsync("deck", null, "owner"));
        await h.Blocked.Task;
        var end = presenter.EndAsync(EndReasons.User);
        h.Release.SetResult();
        await h.LoaderCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False((await start).Started);
        await end;
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        await presenter.WaitUntilIdleAsync();
        Assert.Equal(0, h.Created);
        Assert.Equal(EndReasons.User, Assert.Single(h.Closed).EndReason);
        Assert.Equal("idle", presenter.Snapshot().State);
    }

    [Fact]
    public async Task Abort_pending_start_racing_acceptance_creates_no_upstream_and_a_later_start_is_accepted()
    {
        var h = CreateStartRace();
        await using var presenter = h.Presenter;
        var start = h.BlockNextState("connecting", () => presenter.StartAsync("deck", null, "owner"));
        await h.Blocked.Task;
        presenter.AbortPendingStart();
        h.Release.SetResult();
        Assert.False((await start).Started);
        await presenter.WaitUntilIdleAsync();
        Assert.Equal(0, h.Created);
        Assert.Equal("idle", presenter.Snapshot().State);

        Assert.True((await presenter.StartAsync("deck", null, "owner")).Started);
        Assert.Equal(1, h.Created);
    }

    private static StartRace CreateStartRace(Func<CancellationToken, Task>? load = null) => new(load);

    /// <summary>A presenter whose loop can be held inside a State notification, with a counting upstream.</summary>
    private sealed class StartRace
    {
        private int _created;
        private string? _blockOn;

        public StartRace(Func<CancellationToken, Task>? load)
        {
            Presenter = new Presenter((_, _) => { Interlocked.Increment(ref _created); return new FakeSession(); },
                async (_, id, token) =>
                {
                    LoaderEntered.TrySetResult();
                    try { if (load is not null) await load(token); }
                    catch (OperationCanceledException) { LoaderCancelled.TrySetResult(); throw; }
                    return new LoadedPresentation(id, new PresentationMeta(id, "Title", "deck", "show", null, null, null),
                        [new Slide(0, 1, "One", "Narration.", null)], null);
                }, new PresenterSettings(MaxTalkMinutes: 5), Clock);
            Presenter.Closed += Closed.Add;
            Presenter.State += snapshot =>
            {
                if (snapshot.State != Volatile.Read(ref _blockOn)) return;
                Volatile.Write(ref _blockOn, null);
                Blocked.TrySetResult();
                Release.Task.Wait(); // holds the presenter loop
            };
        }

        public Presenter Presenter { get; }
        public FakeTimeProvider Clock { get; } = new();
        public int Created => Volatile.Read(ref _created);
        public List<PresenterClosed> Closed { get; } = [];
        public TaskCompletionSource LoaderEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LoaderCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public T BlockNextState<T>(string state, Func<T> action)
        {
            Volatile.Write(ref _blockOn, state);
            return action();
        }
    }

    private static Harness Create(Func<int, FakeSession>? factory = null, ITool? tool = null)
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
            [new Slide(0, 1, "One", "Narration.", null)], null)),
            new PresenterSettings(MaxTalkMinutes: 5), clock,
            loadSessionTools: tool is null ? null : (_, _) => Task.FromResult(new SessionToolSet([tool])));
        return new Harness(presenter, clock, sessions);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(Presenter presenter, FakeTimeProvider clock, List<FakeSession> sessions)
        {
            Presenter = presenter;
            Clock = clock;
            Sessions = sessions;
            presenter.Closed += Closed.Add;
        }

        public Presenter Presenter { get; }
        public FakeTimeProvider Clock { get; }
        public List<FakeSession> Sessions { get; }
        public List<PresenterClosed> Closed { get; } = [];
        public Task<PresenterStartResult> Start() => Presenter.StartAsync("deck", null, "owner");
        public Task Settle() => Presenter.WaitUntilIdleAsync();
        public async Task CandidateCreated()
        {
            while (Sessions.Count == 0) await Task.Yield();
        }
        public ValueTask DisposeAsync() => Presenter.DisposeAsync();
    }

    private sealed class CancellableTool(bool requiresConfirmation) : ITool
    {
        public string Name => "cancellable_tool";
        public string Description => "Wait until the run ends";
        public JsonObject Parameters => new() { ["type"] = "object" };
        public IReadOnlyList<string> Tags => [];
        public bool Pinned => false;
        public bool RequiresConfirmation => requiresConfirmation;
        public TimeSpan Timeout => TimeSpan.FromMinutes(5);
        public string Source => "test";
        public string Title => "Test tool";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); }
            return ToolResult.Failure("cancelled") with { Outcome = "cancelled" };
        }
    }
}
