using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PresenterAi.Application.Scripts.Revisions;

/// <summary>
/// Owns every script write except import (plan 010 §4.1, T6). Singleton; it never holds a store: each store operation
/// (head read, one append transaction, revision read) runs in its own async scope, so no <c>DbContext</c> lives across
/// the reviser await and no two presentation workers share one.
/// <list type="bullet">
/// <item>All service state (head snapshots, per-edit outcomes, workers, talks) is guarded by one lock, <c>_gate</c>,
/// never held across an await. A worker writes the new head and the edit's terminal <c>applied</c> outcome in the same
/// critical section, and <see cref="GetReconciliationSnapshot"/> reads both in one, so no snapshot shows
/// <c>applied(vN)</c> with a head older than vN.</item>
/// <item>One FIFO worker per presentation (bounded channel, capacity <see cref="QueueCapacity"/>); every edit is
/// revised, validated and composed on the newest head, committed with a version CAS, and rebuilt once on a
/// conflict.</item>
/// <item>End ↔ commit linearization per talk: a worker's commit phase holds the registration's <c>CommitLock</c>;
/// <see cref="CloseTalkAsync"/> waits for it at most <see cref="CloseBound"/>, then marks the talk closed (one-shot,
/// cancels the talk's token) and releases only a permit it acquired.</item>
/// <item><see cref="Changed"/> is a payload-free signal; receivers reconcile to state.</item>
/// </list>
/// </summary>
public sealed class ScriptRevisionService : IScriptRevisionService, IAsyncDisposable, IDisposable
{
    public const int QueueCapacity = 8;
    public const int MaxOutcomesPerPresentation = 64;
    public static readonly TimeSpan CloseBound = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DisposeBound = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IScriptReviser _reviser;
    private readonly TimeSpan _reviserTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ScriptRevisionService> _logger;
    private readonly CancellationTokenSource _lifetime = new();

    // Everything below is guarded by _gate.
    private readonly Dictionary<string, HeadSnapshot> _heads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EditState> _edits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<EditState>> _editsByPresentation = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Worker> _workers = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _workerTasks = [];
    private readonly Dictionary<string, TalkEntry> _talks = new(StringComparer.Ordinal);
    private long _nextEdit;
    private bool _disposed;

    public ScriptRevisionService(
        IServiceScopeFactory scopeFactory,
        IScriptReviser reviser,
        TrainingOptions options,
        TimeProvider? timeProvider = null,
        ILogger<ScriptRevisionService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(reviser);
        ArgumentNullException.ThrowIfNull(options);
        _scopeFactory = scopeFactory;
        _reviser = reviser;
        _reviserTimeout = options.ReviserTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ScriptRevisionService>.Instance;
    }

    public event Action<string>? Changed;

    public bool IsAvailable => _reviser.IsAvailable;

    /// <summary>Presentations with a running worker (diagnostics and tests).</summary>
    public int ActiveWorkerCount
    {
        get
        {
            lock (_gate)
            {
                return _workers.Count;
            }
        }
    }

    public string Enqueue(string talkId, ScriptEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(talkId);
        ArgumentNullException.ThrowIfNull(request);
        string id;
        string? failure = null;
        lock (_gate)
        {
            id = $"edit_{++_nextEdit}";
            var edit = new EditState(id, talkId, request, request.TargetSlideIndexes.ToArray());
            AddEditLocked(edit);
            if (_disposed)
            {
                failure = ScriptEditErrors.Cancelled;
            }
            else if (!_talks.TryGetValue(talkId, out var talk)
                || !string.Equals(talk.Registration.PresentationId, request.PresentationId, StringComparison.Ordinal)
                || !string.Equals(talk.Registration.OwnerId, request.OwnerId, StringComparison.Ordinal))
            {
                failure = ScriptEditErrors.NotPresenting;
            }
            else if (talk.Registration.IsClosed)
            {
                failure = ScriptEditErrors.Cancelled;
            }
            else
            {
                talk.EditIds.Add(id);
                var created = false;
                if (!_workers.TryGetValue(request.PresentationId, out var worker))
                {
                    worker = new Worker(request.PresentationId);
                    created = true;
                }

                if (!worker.Channel.Writer.TryWrite(new WorkItem(edit, talk)))
                {
                    failure = ScriptEditErrors.QueueFull;
                }
                else if (created)
                {
                    _workers[request.PresentationId] = worker;
                    var task = Task.Run(() => RunWorkerAsync(worker));
                    _workerTasks.Add(task);
                    _ = task.ContinueWith(
                        completed =>
                        {
                            lock (_gate)
                            {
                                _workerTasks.Remove(completed);
                            }
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }

            edit.Outcome = failure is null ? EditOutcome.Queued(edit.Targets) : EditOutcome.Failed(edit.Targets, failure);
        }

        if (failure is not null)
        {
            _logger.LogInformation("edit: failed {EditId} ({Reason})", id, failure);
            RaiseChanged(request.PresentationId);
        }
        else
        {
            _logger.LogInformation("edit: queued {EditId} slide {Slides}", id, FormatSlides(request.TargetSlideIndexes));
        }

        return id;
    }

    public async Task<RevertResult> RevertAsync(
        string ownerId,
        string presentationId,
        int number,
        string userId,
        CancellationToken requestAborted = default)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(presentationId);
        ArgumentNullException.ThrowIfNull(userId);
        RevisionRecord? target = null;
        PresentationScript? targetScript = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var head = await ReadHeadAsync(ownerId, presentationId, requestAborted).ConfigureAwait(false);
            if (head is null)
            {
                return new RevertResult.PresentationNotFound();
            }

            if (target is null)
            {
                target = await ReadRevisionAsync(ownerId, presentationId, number, requestAborted).ConfigureAwait(false);
                if (target is null)
                {
                    return new RevertResult.RevisionNotFound();
                }

                targetScript = ScriptParser.Parse(target.Script, presentationId);
            }

            var changed = ChangedIndexes(head.Slides, targetScript!.Slides);
            var summary = $"Reverted to version {number}";
            var revision = new NewRevision(target.Script, RevisionSources.Revert, summary, head.Version, number, changed, userId);
            AppendResult result;
            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IPresentationRevisionStore>();
                result = await store
                    .TryAppendAsync(ownerId, presentationId, head.Version, revision, requestAborted)
                    .ConfigureAwait(false);
            }

            switch (result)
            {
                case AppendResult.Applied applied:
                    IReadOnlyList<PendingEdit> pending;
                    lock (_gate)
                    {
                        ObserveLocked(new HeadSnapshot(presentationId, applied.Version, targetScript.Slides));
                        pending = PendingEditsLocked(presentationId);
                    }

                    _logger.LogInformation(
                        "edit: reverted {PresentationId} to v{Number} as v{Version}",
                        presentationId,
                        number,
                        applied.Version);
                    RaiseChanged(presentationId);
                    var stored = await ReadRevisionAsync(ownerId, presentationId, applied.Version, CancellationToken.None)
                        .ConfigureAwait(false);
                    var info = stored?.Info ?? new RevisionInfo(
                        applied.Version,
                        RevisionSources.Revert,
                        _timeProvider.GetUtcNow(),
                        summary,
                        head.Version,
                        number,
                        changed,
                        userId);
                    return new RevertResult.Reverted(info, pending);
                case AppendResult.NotFound:
                    return new RevertResult.PresentationNotFound();
            }

            // Conflict: another writer moved the head; retry once on the new head.
        }

        return new RevertResult.Conflict();
    }

    public void Observe(HeadSnapshot head)
    {
        ArgumentNullException.ThrowIfNull(head);
        bool moved;
        lock (_gate)
        {
            moved = ObserveLocked(head);
        }

        if (moved)
        {
            RaiseChanged(head.PresentationId);
        }
    }

    public ReconciliationSnapshot GetReconciliationSnapshot(string presentationId, IReadOnlyCollection<string> localEditIds)
    {
        ArgumentNullException.ThrowIfNull(presentationId);
        ArgumentNullException.ThrowIfNull(localEditIds);
        lock (_gate)
        {
            var outcomes = new Dictionary<string, EditOutcome>(StringComparer.Ordinal);
            foreach (var id in localEditIds)
            {
                if (_edits.TryGetValue(id, out var edit)
                    && string.Equals(edit.Request.PresentationId, presentationId, StringComparison.Ordinal))
                {
                    outcomes[id] = edit.Outcome;
                }
            }

            return new ReconciliationSnapshot(_heads.GetValueOrDefault(presentationId), outcomes);
        }
    }

    public TalkRegistration OpenTalk(string talkId, string ownerId, string presentationId, CancellationToken ticketToken)
    {
        ArgumentNullException.ThrowIfNull(talkId);
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(presentationId);
        TalkEntry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_talks.ContainsKey(talkId))
            {
                throw new InvalidOperationException($"Talk {talkId} is already open.");
            }

            entry = new TalkEntry(new TalkRegistration(talkId, ownerId, presentationId, _lifetime.Token));
            _talks[talkId] = entry;
        }

        if (ticketToken.CanBeCanceled)
        {
            // The one off-loop close path: End / max length / abort cancel the talk's StartTicket while the presenter
            // loop may be blocked; this closes the talk (and cancels a hung reviser call) without the loop.
            var ticketRegistration = ticketToken.Register(() => ObserveClose(CloseEntryAsync(entry)));
            var dispose = false;
            lock (_gate)
            {
                if (entry.CloseCompleted)
                {
                    dispose = true;
                }
                else
                {
                    entry.TicketRegistration = ticketRegistration;
                }
            }

            if (dispose)
            {
                ticketRegistration.Unregister();
            }
        }

        return entry.Registration;
    }

    public Task CloseTalkAsync(string talkId)
    {
        ArgumentNullException.ThrowIfNull(talkId);
        TalkEntry? entry;
        lock (_gate)
        {
            entry = _talks.GetValueOrDefault(talkId);
        }

        return entry is null ? Task.CompletedTask : CloseEntryAsync(entry);
    }

    public async ValueTask DisposeAsync()
    {
        Task[] workers;
        if (!BeginDispose(out workers))
        {
            return;
        }

        try
        {
            await Task.WhenAll(workers).WaitAsync(DisposeBound, _timeProvider).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("edit: {Count} revision worker(s) still running after {Seconds} s", workers.Length, DisposeBound.TotalSeconds);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "edit: revision worker failed during shutdown");
        }
    }

    public void Dispose()
    {
        // Synchronous container disposal: stop everything without waiting (workers observe the lifetime token).
        BeginDispose(out _);
    }

    private bool BeginDispose(out Task[] workers)
    {
        TalkEntry[] talks;
        lock (_gate)
        {
            if (_disposed)
            {
                workers = [];
                return false;
            }

            _disposed = true;
            foreach (var worker in _workers.Values)
            {
                worker.Channel.Writer.TryComplete();
            }

            workers = _workerTasks.ToArray();
            talks = _talks.Values.ToArray();
        }

        foreach (var talk in talks)
        {
            talk.TicketRegistration.Unregister();
        }

        try
        {
            _lifetime.Cancel();
        }
        catch (AggregateException exception)
        {
            _logger.LogWarning(exception, "edit: cancellation callback failed during shutdown");
        }

        return true;
    }

    // ---- worker ------------------------------------------------------------------------------------------------

    private async Task RunWorkerAsync(Worker worker)
    {
        while (true)
        {
            WorkItem? item;
            lock (_gate)
            {
                if (!worker.Channel.Reader.TryRead(out item))
                {
                    // Removed under the lock, so a racing Enqueue either wrote before this check or starts a new worker.
                    if (_workers.TryGetValue(worker.PresentationId, out var current) && ReferenceEquals(current, worker))
                    {
                        _workers.Remove(worker.PresentationId);
                    }

                    worker.Channel.Writer.TryComplete();
                    return;
                }
            }

            try
            {
                await ProcessAsync(item).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "edit: worker failed on {EditId}", item.Edit.Id);
                Finish(item.Edit, ScriptEditErrors.Upstream);
            }
        }
    }

    private async Task ProcessAsync(WorkItem item)
    {
        var edit = item.Edit;
        var talk = item.Talk;
        bool held;
        lock (_gate)
        {
            // A reference keeps the talk's token and lock alive while this edit uses them.
            held = TryAcquireLocked(talk);
        }

        if (!held)
        {
            Finish(edit, ScriptEditErrors.Cancelled);
            return;
        }

        try
        {
            var token = talk.Registration.Cts.Token;
            if (talk.Registration.IsClosed || token.IsCancellationRequested)
            {
                // Closed talk or shutting down: queued items fail without a model call.
                Finish(edit, ScriptEditErrors.Cancelled);
                return;
            }

            if (SetProgress(edit, EditOutcome.Processing(edit.Targets)))
            {
                RaiseChanged(edit.Request.PresentationId);
            }

            await RunEditAsync(edit, talk, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Finish(edit, ScriptEditErrors.Cancelled);
        }
        finally
        {
            ReleaseReference(talk);
        }
    }

    private async Task RunEditAsync(EditState edit, TalkEntry talk, CancellationToken token)
    {
        var request = edit.Request;
        var started = _timeProvider.GetTimestamp();
        var head = await ReadHeadAsync(request.OwnerId, request.PresentationId, token).ConfigureAwait(false);
        if (head is null)
        {
            Finish(edit, ScriptEditErrors.Cancelled);
            return;
        }

        _logger.LogInformation("edit: processing {EditId} on v{Version}", edit.Id, head.Version);
        var prepared = await PrepareAsync(edit, head, token).ConfigureAwait(false);
        if (prepared.Error is { } error)
        {
            Finish(edit, error);
            return;
        }

        var result = await CommitAsync(edit, talk, head.Version, prepared, token).ConfigureAwait(false);
        if (result is AppendResult.Conflict)
        {
            // Rebuild once on the newest head: reuse the rewrite when the targets did not change, else revise again.
            var newer = await ReadHeadAsync(request.OwnerId, request.PresentationId, token).ConfigureAwait(false);
            if (newer is null)
            {
                Finish(edit, ScriptEditErrors.Cancelled);
                return;
            }

            _logger.LogInformation("edit: rebuilding {EditId} on v{Version}", edit.Id, newer.Version);
            prepared = SameTargetNarration(head, newer, edit.Targets)
                ? Recompose(newer, prepared)
                : await PrepareAsync(edit, newer, token).ConfigureAwait(false);
            if (prepared.Error is { } rebuildError)
            {
                Finish(edit, rebuildError);
                return;
            }

            result = await CommitAsync(edit, talk, newer.Version, prepared, token).ConfigureAwait(false);
        }

        switch (result)
        {
            case AppendResult.Applied applied:
                _logger.LogInformation(
                    "edit: applied {EditId} → v{Version} (slide {Slides}) in {Seconds:0.0} s",
                    edit.Id,
                    applied.Version,
                    FormatSlides(prepared.ChangedSlideIndexes),
                    _timeProvider.GetElapsedTime(started).TotalSeconds);
                break;
            case AppendResult.Conflict:
                Finish(edit, ScriptEditErrors.Conflict);
                break;
            default:
                // Talk closed before the commit phase, or the presentation is gone.
                Finish(edit, ScriptEditErrors.Cancelled);
                break;
        }
    }

    /// <summary>Reviser call (bounded by the talk token and <c>Training:ReviserTimeoutSeconds</c>), validate, compose.</summary>
    private async Task<Prepared> PrepareAsync(EditState edit, PresentationHead head, CancellationToken token)
    {
        var slides = head.Slides;
        var targets = edit.Targets.Distinct().Order().ToArray();
        if (targets.Length == 0 || targets.Any(index => index < 0 || index >= slides.Count))
        {
            return Prepared.Failed(ScriptEditErrors.InvalidOutput);
        }

        var request = edit.Request;
        var revisionRequest = new ScriptRevisionRequest(
            head.Script.Meta.Title,
            slides.Select(slide => new ScriptOutlineEntry(slide.Number, slide.Title)).ToArray(),
            targets.Select(index => slides[index])
                .Select(slide => new ScriptRevisionTarget(slide.Number, slide.Title, slide.Narration, slide.Notes))
                .ToArray(),
            request.Feedback,
            request.Exchange,
            request.Recent);

        ScriptRevisionResult result;
        using (var timeout = new CancellationTokenSource(_reviserTimeout, _timeProvider))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token))
        {
            try
            {
                result = await _reviser.ReviseAsync(revisionRequest, linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                return Prepared.Failed(ScriptEditErrors.Timeout);
            }

            token.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested && result is ScriptRevisionResult.Failed)
            {
                return Prepared.Failed(ScriptEditErrors.Timeout);
            }
        }

        if (result is ScriptRevisionResult.Failed failed)
        {
            return Prepared.Failed(failed.Reason);
        }

        var ok = (ScriptRevisionResult.Ok)result;
        var targetNumbers = targets.Select(index => slides[index].Number).ToArray();
        switch (ScriptRevisionValidator.Validate(slides, targetNumbers, ok))
        {
            case ScriptRevisionValidation.Invalid invalid:
                _logger.LogInformation("edit: {EditId} rejected reviser output: {Detail}", edit.Id, invalid.Detail);
                return Prepared.Failed(ScriptEditErrors.InvalidOutput);
            case ScriptRevisionValidation.Valid valid:
                return Compose(edit, head, valid.Changed, valid.Summary);
            default:
                return Prepared.Failed(ScriptEditErrors.InvalidOutput);
        }
    }

    private Prepared Recompose(PresentationHead head, Prepared previous) =>
        Compose(null, head, previous.Changes, previous.Summary);

    private Prepared Compose(EditState? edit, PresentationHead head, IReadOnlyList<RevisedSlide> changes, string summary)
    {
        switch (ScriptRevisionComposer.Compose(head.Script, changes))
        {
            case ScriptComposition.Composed composed when composed.ChangedSlideIndexes.Count > 0:
                return new Prepared(null, composed.Markdown, composed.Script, composed.ChangedSlideIndexes, changes, summary);
            case ScriptComposition.Failed failed:
                _logger.LogInformation("edit: {EditId} compose failed: {Detail}", edit?.Id, failed.Detail);
                return Prepared.Failed(ScriptEditErrors.InvalidOutput);
            default:
                return Prepared.Failed(ScriptEditErrors.InvalidOutput);
        }
    }

    /// <summary>
    /// The commit phase: the talk's commit lock is held from the <c>Closed</c> check to the end of the append and the
    /// state update, so a close either waits for this commit or makes sure it never starts.
    /// </summary>
    private async Task<AppendResult?> CommitAsync(
        EditState edit,
        TalkEntry talk,
        int expectedVersion,
        Prepared prepared,
        CancellationToken token)
    {
        var request = edit.Request;
        var commitLock = talk.Registration.CommitLock;
        await commitLock.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        var applied = false;
        try
        {
            if (talk.Registration.IsClosed)
            {
                return null;
            }

            var revision = new NewRevision(
                prepared.Markdown!,
                RevisionSources.LiveEdit,
                prepared.Summary,
                expectedVersion,
                null,
                prepared.ChangedSlideIndexes,
                request.OwnerId);
            AppendResult result;
            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IPresentationRevisionStore>();
                result = await store
                    .TryAppendAsync(request.OwnerId, request.PresentationId, expectedVersion, revision, token)
                    .ConfigureAwait(false);
            }

            if (result is AppendResult.Applied committed)
            {
                lock (_gate)
                {
                    // One critical section: the new head and the terminal outcome become visible together.
                    ObserveLocked(new HeadSnapshot(request.PresentationId, committed.Version, prepared.Script!.Slides));
                    SetTerminalLocked(edit, EditOutcome.Applied(edit.Targets, committed.Version, prepared.Summary));
                }

                applied = true;
            }

            return result;
        }
        finally
        {
            commitLock.Release();
            if (applied)
            {
                RaiseChanged(request.PresentationId);
            }
        }
    }

    // ---- talks -------------------------------------------------------------------------------------------------

    private async Task CloseEntryAsync(TalkEntry entry)
    {
        lock (_gate)
        {
            if (!TryAcquireLocked(entry))
            {
                return;
            }
        }

        try
        {
            var registration = entry.Registration;
            var acquired = false;
            using (var bound = new CancellationTokenSource(CloseBound, _timeProvider))
            {
                try
                {
                    await registration.CommitLock.WaitAsync(bound.Token).ConfigureAwait(false);
                    acquired = true;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning(
                        "edit: close waited {Seconds} s for an in-flight commit (talk {TalkId})",
                        CloseBound.TotalSeconds,
                        registration.TalkId);
                }
            }

            bool closedNow;
            try
            {
                closedNow = registration.MarkClosed();
            }
            finally
            {
                // Only a permit this caller acquired is ever released.
                if (acquired)
                {
                    registration.CommitLock.Release();
                }
            }

            if (!closedNow)
            {
                return;
            }

            CancellationTokenRegistration ticket;
            string presentationId;
            lock (_gate)
            {
                entry.CloseCompleted = true;
                ticket = entry.TicketRegistration;
                entry.TicketRegistration = default;
                presentationId = registration.PresentationId;
                if (_talks.TryGetValue(registration.TalkId, out var current) && ReferenceEquals(current, entry))
                {
                    _talks.Remove(registration.TalkId);
                }

                PruneTalkLocked(entry);
            }

            ticket.Unregister();
            _logger.LogInformation("edit: talk {TalkId} closed", registration.TalkId);

            // The registration's own reference; the lock and token are disposed once no edit uses them.
            ReleaseReference(entry);
        }
        finally
        {
            ReleaseReference(entry);
        }
    }

    private void ObserveClose(Task close) =>
        _ = close.ContinueWith(
            task => _logger.LogError(task.Exception, "edit: closing a talk failed"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static bool TryAcquireLocked(TalkEntry entry)
    {
        if (entry.References == 0)
        {
            return false;
        }

        entry.References++;
        return true;
    }

    private void ReleaseReference(TalkEntry entry)
    {
        bool dispose;
        lock (_gate)
        {
            entry.References--;
            dispose = entry.References == 0;
        }

        if (dispose)
        {
            entry.Registration.Cts.Dispose();
            entry.Registration.CommitLock.Dispose();
        }
    }

    // ---- state (callers hold _gate unless noted) ----------------------------------------------------------------

    private async Task<PresentationHead?> ReadHeadAsync(string ownerId, string presentationId, CancellationToken token)
    {
        PresentationHead? head;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IPresentationRevisionStore>();
            head = await store.GetHeadAsync(ownerId, presentationId, token).ConfigureAwait(false);
        }

        if (head is not null)
        {
            Observe(head.ToSnapshot());
        }

        return head;
    }

    private async Task<RevisionRecord?> ReadRevisionAsync(
        string ownerId,
        string presentationId,
        int number,
        CancellationToken token)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IPresentationRevisionStore>();
        return await store.GetAsync(ownerId, presentationId, number, token).ConfigureAwait(false);
    }

    private bool ObserveLocked(HeadSnapshot head)
    {
        if (_heads.TryGetValue(head.PresentationId, out var current) && head.Version <= current.Version)
        {
            return false;
        }

        _heads[head.PresentationId] = head;
        return true;
    }

    private void AddEditLocked(EditState edit)
    {
        _edits[edit.Id] = edit;
        if (!_editsByPresentation.TryGetValue(edit.Request.PresentationId, out var list))
        {
            list = [];
            _editsByPresentation[edit.Request.PresentationId] = list;
        }

        list.Add(edit);
        // Cap: drop the oldest terminal outcomes (queued and processing ones are never dropped).
        for (var i = 0; list.Count > MaxOutcomesPerPresentation && i < list.Count;)
        {
            if (list[i].Outcome.IsTerminal)
            {
                _edits.Remove(list[i].Id);
                list.RemoveAt(i);
            }
            else
            {
                i++;
            }
        }
    }

    private void PruneTalkLocked(TalkEntry talk)
    {
        foreach (var id in talk.EditIds)
        {
            if (_edits.Remove(id, out var edit)
                && _editsByPresentation.TryGetValue(edit.Request.PresentationId, out var list))
            {
                list.Remove(edit);
                if (list.Count == 0)
                {
                    _editsByPresentation.Remove(edit.Request.PresentationId);
                }
            }
        }

        talk.EditIds.Clear();
    }

    private IReadOnlyList<PendingEdit> PendingEditsLocked(string presentationId) =>
        _editsByPresentation.TryGetValue(presentationId, out var list)
            ? list.Where(edit => !edit.Outcome.IsTerminal)
                .Select(edit => new PendingEdit(edit.Id, edit.Targets, edit.Outcome.Status))
                .ToArray()
            : [];

    private static bool SetTerminalLocked(EditState edit, EditOutcome outcome)
    {
        if (edit.Outcome.IsTerminal)
        {
            return false;
        }

        edit.Outcome = outcome;
        return true;
    }

    private bool SetProgress(EditState edit, EditOutcome outcome)
    {
        lock (_gate)
        {
            if (edit.Outcome.IsTerminal || edit.Outcome.Status == outcome.Status)
            {
                return false;
            }

            edit.Outcome = outcome;
            return true;
        }
    }

    /// <summary>Terminal failure (not under the lock); written once, then <see cref="Changed"/>.</summary>
    private void Finish(EditState edit, string error)
    {
        bool changed;
        lock (_gate)
        {
            changed = SetTerminalLocked(edit, EditOutcome.Failed(edit.Targets, error));
        }

        if (changed)
        {
            _logger.LogInformation("edit: failed {EditId} ({Reason})", edit.Id, error);
            RaiseChanged(edit.Request.PresentationId);
        }
    }

    private void RaiseChanged(string presentationId)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<string>>())
        {
            try
            {
                handler(presentationId);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "edit: a Changed handler failed");
            }
        }
    }

    private static bool SameTargetNarration(PresentationHead before, PresentationHead after, IReadOnlyList<int> targets) =>
        before.Slides.Count == after.Slides.Count
        && targets.All(index => index >= 0
            && index < before.Slides.Count
            && string.Equals(before.Slides[index].Narration, after.Slides[index].Narration, StringComparison.Ordinal));

    private static int[] ChangedIndexes(IReadOnlyList<Slide> before, IReadOnlyList<Slide> after)
    {
        var changed = new List<int>();
        for (var i = 0; i < Math.Max(before.Count, after.Count); i++)
        {
            if (i >= before.Count || i >= after.Count
                || !string.Equals(before[i].Narration, after[i].Narration, StringComparison.Ordinal))
            {
                changed.Add(i);
            }
        }

        return changed.ToArray();
    }

    private static string FormatSlides(IEnumerable<int> indexes) => string.Join(",", indexes.Select(index => index + 1));

    private sealed class EditState(string id, string talkId, ScriptEditRequest request, IReadOnlyList<int> targets)
    {
        public string Id { get; } = id;

        public string TalkId { get; } = talkId;

        public ScriptEditRequest Request { get; } = request;

        public IReadOnlyList<int> Targets { get; } = targets;

        /// <summary>Guarded by the service lock; a terminal value is never replaced.</summary>
        public EditOutcome Outcome { get; set; } = EditOutcome.Queued(targets);
    }

    private sealed record WorkItem(EditState Edit, TalkEntry Talk);

    private sealed class Worker(string presentationId)
    {
        public string PresentationId { get; } = presentationId;

        public Channel<WorkItem> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<WorkItem>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true
            });
    }

    private sealed class TalkEntry(TalkRegistration registration)
    {
        public TalkRegistration Registration { get; } = registration;

        /// <summary>Guarded by the service lock: 1 for the open registration plus one per user (edit, closer).</summary>
        public int References { get; set; } = 1;

        public bool CloseCompleted { get; set; }

        public CancellationTokenRegistration TicketRegistration { get; set; }

        public List<string> EditIds { get; } = [];
    }

    private sealed record Prepared(
        string? Error,
        string? Markdown,
        PresentationScript? Script,
        IReadOnlyList<int> ChangedSlideIndexes,
        IReadOnlyList<RevisedSlide> Changes,
        string Summary)
    {
        public static Prepared Failed(string error) => new(error, null, null, [], [], string.Empty);
    }
}
