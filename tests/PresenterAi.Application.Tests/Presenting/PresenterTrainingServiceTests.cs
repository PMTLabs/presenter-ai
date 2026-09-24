using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Scripts;
using PresenterAi.Application.Scripts.Revisions;
using Xunit;

namespace PresenterAi.Application.Tests.Presenting;

/// <summary>
/// Plan 010 T11 (lane C follow-up): the key presenter training flows against the real <see cref="ScriptRevisionService"/>
/// over the in-memory store (instead of the scriptable fake of <c>PresenterTrainingTests</c>), with a fake reviser and
/// <see cref="FakeSession"/>. The loader reads the store's head, as the Postgres loader reads <c>presentations.script</c>.
/// </summary>
public sealed class PresenterTrainingServiceTests
{
    private const string Owner = "owner";
    private const string Pid = "sample";

    [Fact]
    public async Task Voice_edit_through_the_real_service_is_applied_replayed_and_versioned()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();

        h.S.RaiseToolCall("d", "c1", "revise_script", "{\"feedback\":\"Mention the 2025 figures.\"}");
        await Eventually(() => h.S.Sent.Any(s => s.EventId == "c1"));
        await h.Settle();
        await h.Advance(TimeSpan.FromSeconds(8));
        h.S.Hear("yes", 2000, 2100);
        await h.Settle();
        await h.Advance(TimeSpan.FromMilliseconds(701));

        await Eventually(() => h.Edits.Any(e => e.Status == ScriptEditStatus.Applied));
        await h.Settle();
        var applied = Assert.Single(h.Edits, e => e.Status == ScriptEditStatus.Applied);
        Assert.Equal(2, applied.Version);
        Assert.Equal([0], applied.SlideIndexes);
        var call = Assert.Single(h.Reviser.Calls);
        Assert.Equal("Mention the 2025 figures.", call.Feedback);
        Assert.Equal([1], call.Targets.Select(t => t.Number));

        // The version event precedes the acknowledgement, and the current slide is replayed with the new narration.
        var versionAt = h.Timeline.IndexOf("version:2");
        Assert.True(versionAt >= 0 && versionAt < h.Timeline.IndexOf($"edit:{applied.Id}:applied"));
        var newNarration = h.Original.Slides[0].Narration + " Mention the 2025 figures.";
        Assert.Contains(h.S.Sent, s => s.Content?.Contains("Mention the 2025 figures.", StringComparison.Ordinal) == true);
        Assert.Equal(2, h.Presenter.CurrentScriptVersion()!.Version);

        var head = (await h.Store.GetHeadAsync(Owner, Pid))!;
        Assert.Equal(2, head.Version);
        Assert.Equal(newNarration, head.Slides[0].Narration);
        var history = (await h.Store.ListAsync(Owner, Pid, 1, 10))!;
        Assert.Equal([RevisionSources.LiveEdit, RevisionSources.Import], history.Items.Select(i => i.Source));
    }

    [Fact]
    public async Task End_closes_the_talk_registration_and_cancels_a_hung_reviser()
    {
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        var hold = h.Reviser.HoldNext();
        Assert.True(await h.Presenter.TrainOnTurnAsync(Owner, "Q?", "A.", 0));
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var registration = Assert.Single(h.Talks);
        Assert.False(registration.IsClosed);

        Assert.True(await h.Presenter.EndAsync());
        Assert.True(registration.IsClosed, "End awaits CloseTalkAsync before the talk is over");
        await hold.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Eventually(() => h.Service.ActiveWorkerCount == 0);

        // Nothing more can be enqueued for that talk, and nothing was committed.
        var late = h.Service.Enqueue(registration.TalkId, new ScriptEditRequest(Pid, Owner, [0], 1, "Late.", null, []));
        Assert.Equal(ScriptEditStatus.Failed, h.Service.GetReconciliationSnapshot(Pid, [late]).Outcomes[late].Status);
        Assert.Equal(1, (await h.Store.GetHeadAsync(Owner, Pid))!.Version);
        Assert.DoesNotContain(h.Edits, e => e.Status == ScriptEditStatus.Applied);
        Assert.Single(h.Reviser.Calls);
    }

    [Fact]
    public async Task Max_length_cancels_a_hung_reviser_and_commits_nothing()
    {
        await using var h = new Harness(new PresenterSettings(3000, "marin", 5000, 16, MaxTalkMinutes: 1));
        await h.Start();
        await h.TrainerOn();
        var hold = h.Reviser.HoldNext();
        Assert.True(await h.Presenter.TrainOnTurnAsync(Owner, "Q?", "A.", 0));
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await h.Advance(TimeSpan.FromSeconds(61));
        await Eventually(() => !h.Closed.IsEmpty);
        Assert.True(Assert.Single(h.Talks).IsClosed);
        await hold.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Eventually(() => h.Service.ActiveWorkerCount == 0);

        Assert.Equal(1, (await h.Store.GetHeadAsync(Owner, Pid))!.Version);
        Assert.DoesNotContain(h.Edits, e => e.Status == ScriptEditStatus.Applied);
    }

    [Fact]
    public async Task Next_start_loads_a_commit_made_while_no_talk_was_running()
    {
        await using var h = new Harness();
        await h.Start();
        Assert.True(await h.Presenter.EndAsync());
        await h.Settle();

        // Another writer (an HTTP revert in another process, a CLI import) commits v2 while no talk runs.
        var v2 = h.Markdown.Replace("Hello everyone, and welcome.", "Hello everyone, from the next version.", StringComparison.Ordinal);
        Assert.Equal(new AppendResult.Applied(2), await h.Store.TryAppendAsync(Owner, Pid, 1,
            new NewRevision(v2, RevisionSources.Import, "Re-imported", 1, null, [0], null)));

        var sentBefore = h.Sessions.Count;
        await h.Start();
        Assert.Equal(sentBefore + 1, h.Sessions.Count);
        Assert.Equal(2, h.Versions[^1].Version);
        Assert.Contains(h.S.Sent, s => s.Content?.Contains("from the next version", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(h.S.Sent, s => s.Content?.Contains("Hello everyone, and welcome.", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Page_log_lines_follow_an_edit_from_queue_to_replay_and_failure()
    {
        // Plan 010 §4.3 "Log lines": the presenter page log (sent to the browser as log frames) for one applied and one
        // failed edit on the current slide.
        await using var h = new Harness();
        await h.Start();
        await h.TrainerOn();
        var hold = h.Reviser.HoldNext();
        Assert.True(await h.Presenter.TrainOnTurnAsync(Owner, "Q?", "The programme started in 2020.", 0));
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Eventually(() => h.Logs.Contains("edit: processing edit_1 on v1"));
        hold.Release.SetResult();
        await Eventually(() => h.Logs.Any(l => l.StartsWith("edit: replay slide", StringComparison.Ordinal)));

        h.Reviser.FailNext(ScriptEditErrors.Timeout);
        Assert.True(await h.Presenter.TrainOnTurnAsync(Owner, "Q?", "A.", 0));
        await Eventually(() => h.Logs.Contains("edit: failed edit_2 (timeout)"));

        var logs = h.Logs.ToArray();
        Assert.Contains("trainer: on (voice: yes)", logs);
        Assert.Contains("edit: queued edit_1 slide 1", logs);
        Assert.Contains("edit: hold slide 1", logs);
        Assert.Contains("edit: head reloaded v1 → v2", logs);
        Assert.Contains(logs, l => l.StartsWith("edit: applied edit_1 → v2 (slide 1) in ", StringComparison.Ordinal));
        Assert.Contains("edit: replay slide 1 (v2)", logs);
        Assert.True(Array.IndexOf(logs, "edit: queued edit_1 slide 1") < Array.IndexOf(logs, "edit: processing edit_1 on v1"));
        Assert.True(Array.IndexOf(logs, "edit: head reloaded v1 → v2") < Array.IndexOf(logs, "edit: replay slide 1 (v2)"));
    }

    private static async Task Eventually(Func<bool> predicate)
    {
        for (var i = 0; i < 500; i++)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }

        Assert.True(predicate());
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(PresenterSettings? settings = null)
        {
            Markdown = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.md"));
            Original = ScriptParser.Parse(Markdown, Pid);
            Store = new InMemoryPresentationRevisionStore(timeProvider: Clock);
            Store.Seed(Owner, Pid, Markdown);
            Service = new ScriptRevisionService(
                new StoreScopes(Store),
                Reviser,
                new TrainingOptions { ReviserTimeoutSeconds = 180 },
                Clock);
            Presenter = new Presenter(
                (request, _) =>
                {
                    var session = new FakeSession { Request = request };
                    Sessions.Add(session);
                    return session;
                },
                async (owner, id, ct) =>
                {
                    var head = await Store.GetHeadAsync(owner, id, ct) ?? throw new FileNotFoundException(id);
                    return new LoadedPresentation(id, head.Script.Meta, head.Slides, null, head.Version);
                },
                settings ?? new PresenterSettings(3000, "marin", 5000, 16),
                Clock,
                null,
                _ => true,
                null,
                null,
                new SpyService(Service, Talks));
            Presenter.Closed += closed => Closed.Add(closed);
            Presenter.Log += log => Logs.Enqueue(log.Message);
            Presenter.ScriptEdit += edit => { Edits.Enqueue(edit); Timeline.Add($"edit:{edit.Id}:{edit.Status}"); };
            Presenter.ScriptVersion += version => { Versions.Add(version); Timeline.Add($"version:{version.Version}"); };
        }

        public FakeTimeProvider Clock { get; } = new();
        public string Markdown { get; }
        public PresentationScript Original { get; }
        public InMemoryPresentationRevisionStore Store { get; }
        public FakeReviser Reviser { get; } = new();
        public ScriptRevisionService Service { get; }
        public Presenter Presenter { get; }
        public List<FakeSession> Sessions { get; } = [];
        public ConcurrentBag<PresenterClosed> Closed { get; } = [];
        public ConcurrentQueue<PresenterScriptEdit> Edits { get; } = new();
        public List<PresenterScriptVersion> Versions { get; } = [];
        public List<string> Timeline { get; } = [];
        public ConcurrentQueue<TalkRegistration> Talks { get; } = new();
        public ConcurrentQueue<string> Logs { get; } = new();
        public FakeSession S => Sessions[^1];

        public Task Settle() => Presenter.WaitUntilIdleAsync();

        public async Task Start()
        {
            Assert.True((await Presenter.StartAsync(Pid, null, Owner)).Started);
            await Settle();
        }

        public async Task TrainerOn()
        {
            Assert.True(await Presenter.SetTrainerModeAsync(Owner, true));
            await Settle();
        }

        public async Task Advance(TimeSpan by)
        {
            Clock.Advance(by);
            await Settle();
        }

        public async ValueTask DisposeAsync()
        {
            await Presenter.DisposeAsync();
            await Service.DisposeAsync();
        }
    }

    /// <summary>Every scope resolves the one singleton in-memory store (as the singleton registration does in DI).</summary>
    private sealed class StoreScopes(IPresentationRevisionStore store) : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceProvider ServiceProvider => this;

        public IServiceScope CreateScope() => this;

        public object? GetService(Type serviceType) => serviceType == typeof(IPresentationRevisionStore) ? store : null;

        public void Dispose()
        {
        }
    }

    /// <summary>Delegates to the real service and keeps every registration the presenter opened.</summary>
    private sealed class SpyService(ScriptRevisionService inner, ConcurrentQueue<TalkRegistration> talks) : IScriptRevisionService
    {
        public event Action<string>? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public bool IsAvailable => inner.IsAvailable;

        public string Enqueue(string talkId, ScriptEditRequest request) => inner.Enqueue(talkId, request);

        public Task<RevertResult> RevertAsync(string ownerId, string presentationId, int number, string userId,
            CancellationToken requestAborted = default) => inner.RevertAsync(ownerId, presentationId, number, userId, requestAborted);

        public void Observe(HeadSnapshot head) => inner.Observe(head);

        public ReconciliationSnapshot GetReconciliationSnapshot(string presentationId, IReadOnlyCollection<string> localEditIds) =>
            inner.GetReconciliationSnapshot(presentationId, localEditIds);

        public TalkRegistration OpenTalk(string talkId, string ownerId, string presentationId, CancellationToken ticketToken)
        {
            var registration = inner.OpenTalk(talkId, ownerId, presentationId, ticketToken);
            talks.Enqueue(registration);
            return registration;
        }

        public Task CloseTalkAsync(string talkId) => inner.CloseTalkAsync(talkId);
    }

    /// <summary>Appends the feedback to every target; a held call waits for release or its cancellation.</summary>
    private sealed class FakeReviser : IScriptReviser
    {
        private readonly ConcurrentQueue<Hold> _holds = new();
        private readonly ConcurrentQueue<ScriptRevisionRequest> _calls = new();
        private readonly ConcurrentQueue<string> _failures = new();

        public IReadOnlyList<ScriptRevisionRequest> Calls => _calls.ToArray();

        public Hold HoldNext()
        {
            var hold = new Hold();
            _holds.Enqueue(hold);
            return hold;
        }

        public void FailNext(string reason) => _failures.Enqueue(reason);

        public async Task<ScriptRevisionResult> ReviseAsync(ScriptRevisionRequest request, CancellationToken cancellationToken)
        {
            _calls.Enqueue(request);
            if (_failures.TryDequeue(out var reason)) return new ScriptRevisionResult.Failed(reason);
            if (_holds.TryDequeue(out var hold))
            {
                hold.Entered.TrySetResult();
                try
                {
                    await hold.Release.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    hold.Cancelled.TrySetResult();
                    return new ScriptRevisionResult.Failed(ScriptEditErrors.Cancelled);
                }
            }

            return new ScriptRevisionResult.Ok(
                request.Targets.Select(t => new RevisedSlide(t.Number, t.Narration + " " + request.Feedback)).ToArray(),
                "Fake rewrite");
        }

        public sealed class Hold
        {
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
