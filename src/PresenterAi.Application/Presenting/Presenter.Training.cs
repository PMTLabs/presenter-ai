using System.Text.Json;
using PresenterAi.Application.Scripts;
using PresenterAi.Application.Scripts.Revisions;
using PresenterAi.Application.Tools;
using PresenterAi.Application.Presenting.Tools;

namespace PresenterAi.Application.Presenting;

/// <summary>
/// Plan 010 live training: Trainer mode, the <c>revise_script</c> intent, the narration gate while an edit of the current
/// slide is pending, and the reconcile to the revision service's state. Every field here is loop-only state: it is read
/// and written only by the presenter's single channel consumer. Background work (the service's <c>Changed</c> signal, the
/// keep-alive timer) re-enters through <see cref="QueueFromProducer"/>; the reconcile reads current state with one
/// atomic <see cref="IScriptRevisionService.GetReconciliationSnapshot"/> call instead of applying event payloads.
/// </summary>
public sealed partial class Presenter
{
    public const int EditKeepAliveMs = 30_000;
    public const int MaxTrainingTextChars = 2_000;
    private const int MaxRecentTurns = 8;
    private const int MaxRecentTurnChars = 1_000;

    private readonly IScriptRevisionService? _scriptRevisions;
    private readonly Dictionary<string, LocalEdit> _localEdits = new(StringComparer.Ordinal);
    private readonly List<RecentTranscriptTurn> _recentTurns = [];
    private bool _trainerMode;
    private (string OwnerId, bool On)? _idleTrainerToggle;
    private string? _ownerId;
    private string? _talkId;
    private TalkRegistration? _talkRegistration;
    private int _scriptVersion;
    private bool _replayOnResume;
    private bool _replayOnResumeChanged;
    private bool _headSlideCountWarned;
    private int _rejectedEditSequence;
    private ITimer? _editKeepAlive;
    private long _editKeepAliveGeneration;
    private PresenterScriptVersion? _scriptVersionSnapshot;
    private PresenterTrainerState? _trainerStateSnapshot;

    public event Action<PresenterScriptEdit>? ScriptEdit;
    public event Action<PresenterScriptVersion>? ScriptVersion;
    public event Action<PresenterTrainerState>? TrainerState;

    /// <summary>The last <c>script_version</c> of the running talk (null when no talk runs); for a bridge that connects mid-talk.</summary>
    public PresenterScriptVersion? CurrentScriptVersion() => Volatile.Read(ref _scriptVersionSnapshot);

    /// <summary>The last published Trainer mode (the talk's, or the idle request for the next Start); for a bridge that connects.</summary>
    public PresenterTrainerState CurrentTrainerState() =>
        Volatile.Read(ref _trainerStateSnapshot) ?? new PresenterTrainerState(null, false, TrainerAvailable, true);

    public Task<bool> SetTrainerModeAsync(string ownerId, bool on, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        return EnqueueCommandAsync(new TrainerModeCommand(ownerId, on), cancellationToken);
    }

    public Task<bool> TrainOnTurnAsync(
        string ownerId,
        string question,
        string answer,
        int slideIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(answer);
        return EnqueueCommandAsync(new TrainOnTurnCommand(ownerId, question, answer, slideIndex), cancellationToken);
    }

    private bool TrainerAvailable => _scriptRevisions?.IsAvailable == true;

    private bool VoiceTraining => _sessionInfo?.DelegationMode == "responses";

    private bool TalkRunning => _state is PresenterState.Presenting or PresenterState.Paused;

    /// <summary>
    /// The one narration gate: the current slide is a target of an unsettled local edit. Wrap-up is past every slide, so a
    /// manual Next on a held last slide may still finish the talk.
    /// </summary>
    private bool NarrationHeld => !_wrappingUp && TalkRunning && IsHeld(_slideIndex);

    private bool IsHeld(int slideIndex)
    {
        foreach (var edit in _localEdits.Values)
        {
            if (!edit.Settled && edit.Targets.Contains(slideIndex)) return true;
        }

        return false;
    }

    private void OnScriptRevisionsChanged(string presentationId) =>
        QueueFromProducer(new ReconcileRequested(presentationId));

    private void OnTrainingEvent(TrainingEvent trainingEvent)
    {
        switch (trainingEvent)
        {
            case ReconcileRequested reconcile:
                // A signal for another presentation, or one that arrives outside a running talk, changes nothing here.
                if (TalkRunning && _presentation?.Id == reconcile.PresentationId) ReconcileWithHead();
                break;
            case EditKeepAliveElapsed keepAlive when keepAlive.Generation == _editKeepAliveGeneration:
                _editKeepAlive = null;
                if (TalkRunning && HasUnsettledEdits())
                {
                    RecordActivity();
                    ArmEditKeepAlive();
                }

                break;
        }
    }

    // ---- Start / connect / end hooks -------------------------------------------------------------------------------

    /// <summary>Resets the talk's training state when a Start is accepted (before the load).</summary>
    private void ResetTrainingForStart(string ownerId)
    {
        _ownerId = ownerId;
        _talkId = null;
        _talkRegistration = null;
        _localEdits.Clear();
        _recentTurns.Clear();
        _replayOnResume = false;
        _replayOnResumeChanged = false;
        _headSlideCountWarned = false;
        _trainerMode = false;
        StopEditKeepAlive();
        Volatile.Write(ref _scriptVersionSnapshot, null);
    }

    /// <summary>
    /// Runs once the upstream of a Start is connected (after <c>_session</c> is set): observes the loaded head, opens the
    /// talk registration and applies an idle toggle of the same owner. Earlier Start failures have nothing to leak.
    /// </summary>
    private void OnTrainingTalkConnected(string ownerId, StartTicket ticket)
    {
        var presentation = _presentation!;
        _scriptVersion = presentation.Version ?? 1;
        var toggle = _idleTrainerToggle;
        _idleTrainerToggle = null;
        if (_scriptRevisions is null) return;

        _scriptRevisions.Observe(new HeadSnapshot(presentation.Id, _scriptVersion, presentation.Slides));
        _talkId = $"talk_{Guid.NewGuid():N}";
        _talkRegistration = _scriptRevisions.OpenTalk(_talkId, ownerId, presentation.Id, ticket.Token);
        if (toggle is { } idle && idle.OwnerId == ownerId && idle.On && TrainerAvailable)
        {
            _trainerMode = true;
            LogMessage("info", $"trainer: on (voice: {(VoiceTraining ? "yes" : "no")})");
        }

        // The service may already hold a newer head than the loader returned (a commit that landed meanwhile).
        ReconcileWithHead();
    }

    /// <summary>After Start presented its first slide, and after every reconnect: voice availability may have changed.</summary>
    private void OnTrainingSessionReady(bool reconnected)
    {
        if (_scriptRevisions is null || _talkId is null) return;
        if (reconnected)
        {
            LogMessage("info", $"trainer: {(_trainerMode ? "on" : "off")} (voice: {(VoiceTraining ? "yes" : "no")})");
            ReconcileWithHead();
        }

        EmitScriptVersion();
    }

    /// <summary>Loop-side End: waits (bounded by the service, 5 s) until no commit of this talk is in flight.</summary>
    private async Task CloseTrainingTalkAsync()
    {
        var talkId = _talkRegistration is null ? null : _talkId;
        _talkRegistration = null;
        if (talkId is null || _scriptRevisions is null) return;
        try
        {
            await _scriptRevisions.CloseTalkAsync(talkId).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogMessage("error", $"edit: close failed ({exception.GetType().Name})");
        }
    }

    /// <summary>
    /// Synchronous close from <c>OnClosed</c> (upstream loss, fail-safe close): starts the idempotent close observed and
    /// resets the training state. Trainer mode resets to off when a talk ends.
    /// </summary>
    private void ResetTrainingOnClosed()
    {
        var talkId = _talkRegistration is null ? null : _talkId;
        _talkRegistration = null;
        if (talkId is not null && _scriptRevisions is not null)
        {
            try
            {
                ObserveBackground(_scriptRevisions.CloseTalkAsync(talkId), "edit close");
            }
            catch (Exception exception)
            {
                LogMessage("error", $"edit: close failed ({exception.GetType().Name})");
            }
        }

        _talkId = null;
        _trainerMode = false;
        PublishTrainerState();
        _localEdits.Clear();
        _recentTurns.Clear();
        _replayOnResume = false;
        _replayOnResumeChanged = false;
        StopEditKeepAlive();
        Volatile.Write(ref _scriptVersionSnapshot, null);
    }

    // ---- Commands ---------------------------------------------------------------------------------------------------

    private bool SetTrainerModeCore(string ownerId, bool on)
    {
        if (_state == PresenterState.Idle)
        {
            // Stored with the owner id; honoured only by a Start of that owner, and the next accepted Start consumes it.
            _idleTrainerToggle = (ownerId, on);
            LogMessage("info", $"trainer: {(on ? "on" : "off")} requested for the next talk");
            PublishTrainerState(always: true);
            return true;
        }

        if (!TalkRunning || _talkId is null || ownerId != _ownerId)
        {
            LogMessage("warn", "trainer: toggle refused (not the owner of the running talk)");
            // The client shows only server state: re-announce it so the refused switch settles back.
            PublishTrainerState(always: true);
            return false;
        }

        if (on && !TrainerAvailable)
        {
            _trainerMode = false;
            LogMessage("warn", "trainer: unavailable (no reasoning route)");
            EmitScriptVersion();
            return false;
        }

        _trainerMode = on;
        LogMessage("info", $"trainer: {(on ? "on" : "off")} (voice: {(VoiceTraining ? "yes" : "no")})");
        EmitScriptVersion();
        return true;
    }

    private bool TrainOnTurnCore(string ownerId, string question, string answer, int slideIndex)
    {
        if (!TalkRunning || _talkId is null || _presentation is null)
        {
            RejectEdit(slideIndex, ScriptEditErrors.NotPresenting);
            return false;
        }

        if (ownerId != _ownerId)
        {
            LogMessage("warn", "edit: train on turn refused (not the owner of the running talk)");
            return false;
        }

        if (!_trainerMode)
        {
            RejectEdit(slideIndex, ScriptEditErrors.TrainerModeOff);
            return false;
        }

        if (slideIndex < 0 || slideIndex >= SlideCount || !ValidTrainingText(question) || !ValidTrainingText(answer))
        {
            LogMessage("warn", "edit: train on turn refused (invalid exchange)");
            return false;
        }

        EnqueueEdit(new EditIntent(_talkId, _presentation.Id, ownerId, [slideIndex], _scriptVersion,
            PromptBuilder.TrainOnTurnFeedback, new TrainingExchange(question.Trim(), answer.Trim())));
        return true;
    }

    private static bool ValidTrainingText(string text) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= MaxTrainingTextChars;

    private void RejectEdit(int slideIndex, string error)
    {
        var id = $"rejected_{++_rejectedEditSequence}";
        LogMessage("info", $"edit: failed {id} ({error})");
        ScriptEdit?.Invoke(new PresenterScriptEdit(id, ScriptEditStatus.Failed,
            slideIndex >= 0 ? [slideIndex] : [], null, null, error));
    }

    // ---- revise_script: gate, intent, confirmation ------------------------------------------------------------------

    private static bool IsReviseScript(ITool tool) =>
        string.Equals(tool.Name, ReviseScriptTool.ToolName, StringComparison.Ordinal);

    /// <summary>
    /// Gate for a resolved <c>revise_script</c> call (direct or through <c>call_tool</c>), before the confirmation branch.
    /// Returns the intent to capture, or null after answering the call itself (Trainer mode off, bad arguments).
    /// </summary>
    private EditIntent? GateReviseScript(ToolCallReceived call, JsonElement arguments)
    {
        if (!_trainerMode || _talkId is null || _presentation is null || _ownerId is null)
        {
            LogMessage("info", "edit: revise_script refused (trainer mode off)");
            CompleteImmediateCall(call, ToolResult.Failure($"{ScriptEditErrors.TrainerModeOff}: answer it as a question"));
            return null;
        }

        var feedback = arguments.TryGetProperty("feedback", out var feedbackElement) &&
            feedbackElement.ValueKind == JsonValueKind.String ? feedbackElement.GetString()!.Trim() : "";
        if (feedback.Length is 0 or > MaxTrainingTextChars)
        {
            CompleteImmediateCall(call, ToolResult.Failure($"feedback must be 1 to {MaxTrainingTextChars} characters"));
            return null;
        }

        var targets = new SortedSet<int>();
        if (arguments.TryGetProperty("slide_numbers", out var numbers) && numbers.ValueKind == JsonValueKind.Array)
        {
            foreach (var number in numbers.EnumerateArray())
            {
                if (number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out var value) ||
                    value < 1 || value > SlideCount)
                {
                    CompleteImmediateCall(call, ToolResult.Failure($"there are slides 1 to {SlideCount}"));
                    return null;
                }

                targets.Add(value - 1);
            }
        }

        // Targets are resolved now and never substituted later (navigation after "yes" keeps them).
        if (targets.Count == 0) targets.Add(_slideIndex);
        return new EditIntent(_talkId, _presentation.Id, _ownerId, targets.ToArray(), _scriptVersion, feedback, null);
    }

    /// <summary>
    /// Voice "yes" for a <c>revise_script</c> confirmation, on the loop: enqueues the captured intent synchronously when it
    /// still belongs to this talk. Returns false when the talk moved on (nothing is enqueued).
    /// </summary>
    private bool ApproveScriptEdit(EditIntent intent)
    {
        if (intent.TalkId != _talkId || !TalkRunning || _presentation?.Id != intent.PresentationId ||
            intent.OwnerId != _ownerId)
        {
            LogMessage("info", "edit: approval ignored (the talk moved on)");
            return false;
        }

        EnqueueEdit(intent);
        return true;
    }

    private void OnScriptEditDeclined()
    {
        // Site 18 (plan 011): deferred while an ask exchange runs.
        var eventId = $"edit-declined-{_slideIndex + 1}";
        EmitOrDefer("edit declined", PromptBuilder.ScriptEditDeclinedInstruction(), eventId);
    }

    // ---- Queue, hold, reconcile -------------------------------------------------------------------------------------

    private void EnqueueEdit(EditIntent intent)
    {
        var request = new ScriptEditRequest(intent.PresentationId, intent.OwnerId, intent.Targets, intent.BaseVersion,
            intent.Feedback, intent.Exchange,
            _recentTurns.Select(turn => new RecentTurn(turn.Role, turn.Text.ToString())).ToArray());
        var id = _scriptRevisions!.Enqueue(intent.TalkId, request);
        _localEdits[id] = new LocalEdit(id, intent.Targets, _timeProvider.GetTimestamp());
        LogMessage("info", $"edit: queued {id} slide {SlideList(intent.Targets)}");
        // Site 8 (plan 011): during an ask exchange the notice waits for its end, and so does the hold's flush and
        // instruction (EndExchange re-enters the hold if the slide is still held).
        if (_state == PresenterState.Presenting && NarrationHeld && ExchangeAllows(ModelAction.EnterHold)) EnterHold();
        EmitOrDefer("edit pending", PromptBuilder.ScriptEditPendingInstruction(), $"edit-{id}-pending");
        ScriptEdit?.Invoke(new PresenterScriptEdit(id, ScriptEditStatus.Queued, intent.Targets, null, null, null));
        RecordActivity();
        ArmEditKeepAlive();
        // The outcome may already be terminal (queue_full); reconciling by state is idempotent.
        ReconcileWithHead();
    }

    /// <summary>Stops in-flight narration of the current slide: no timer may send the next part or advance.</summary>
    private void EnterHold()
    {
        ClearSilenceTimer();
        ClearNudgeTimer();
        LogMessage("info", $"edit: hold slide {_slideIndex + 1}");
        Flush?.Invoke();
        var slide = _presentation!.Slides[_slideIndex];
        _session?.AppendInstructions(PromptBuilder.ScriptEditHoldInstruction(_slideIndex, SlideCount, slide.Title),
            $"slide-{_slideIndex + 1}-hold");
    }

    /// <summary>
    /// <c>PresentSlide</c> on a held slide (advance, navigation away and back): no narration, only the hold notice.
    /// </summary>
    private void PresentHeldSlide(int index)
    {
        _parts = Array.Empty<string>();
        _partsSent = 0;
        LogMessage("info", $"edit: hold slide {index + 1}");
        Flush?.Invoke();
        var slide = _presentation!.Slides[index];
        _session?.AppendInstructions(PromptBuilder.ScriptEditHoldInstruction(index, SlideCount, slide.Title),
            $"slide-{index + 1}-hold");
    }

    /// <summary>
    /// Reconcile to state, not events (plan 010 §4.1): one atomic snapshot, then swap the narration to the head, settle
    /// local edits by id exactly once, replay or release the current slide, and emit <c>script_version</c> before the
    /// terminal <c>script_edit</c> frames. Duplicated, late or reordered signals change nothing.
    /// </summary>
    private void ReconcileWithHead()
    {
        if (_scriptRevisions is null || _presentation is null || _talkId is null) return;
        if (_state is not (PresenterState.Connecting or PresenterState.Presenting or PresenterState.Paused)) return;

        var unsettledIds = _localEdits.Values.Where(edit => !edit.Settled).Select(edit => edit.Id).ToArray();
        // 0. One atomic read; everything below uses only this snapshot.
        var snapshot = _scriptRevisions.GetReconciliationSnapshot(_presentation.Id, unsettledIds);
        var wasHeld = NarrationHeld;

        // 1. Swap to a newer head.
        var versionMoved = false;
        var currentChanged = false;
        if (snapshot.Head is { } head && head.Version > _scriptVersion)
        {
            if (head.Slides.Count != _presentation.Slides.Count)
            {
                if (!_headSlideCountWarned)
                {
                    _headSlideCountWarned = true;
                    LogMessage("warn", $"edit: head v{head.Version} has {head.Slides.Count} slides, this talk has " +
                        $"{_presentation.Slides.Count}; it is used from the next Start");
                }
            }
            else
            {
                var changed = new List<int>();
                for (var i = 0; i < head.Slides.Count; i++)
                {
                    if (!string.Equals(head.Slides[i].Narration, _presentation.Slides[i].Narration, StringComparison.Ordinal))
                        changed.Add(i);
                }

                LogMessage("info", $"edit: head reloaded v{_scriptVersion} → v{head.Version}");
                _presentation = _presentation with { Slides = head.Slides, Version = head.Version };
                _scriptVersion = head.Version;
                versionMoved = true;
                currentChanged = changed.Contains(_slideIndex);
            }
        }

        // 2. Settle local edits by id, exactly once.
        var processing = new List<LocalEdit>();
        var settled = new List<(LocalEdit Edit, EditOutcome Outcome)>();
        foreach (var id in unsettledIds)
        {
            var edit = _localEdits[id];
            if (!snapshot.Outcomes.TryGetValue(id, out var outcome))
            {
                // The service no longer knows the edit (pruned): it can never apply to this talk.
                outcome = EditOutcome.Failed(edit.Targets, ScriptEditErrors.Cancelled);
            }

            if (outcome.IsTerminal)
            {
                edit.Settled = true;
                settled.Add((edit, outcome));
            }
            else if (outcome.Status == ScriptEditStatus.Processing && !edit.ProcessingEmitted)
            {
                edit.ProcessingEmitted = true;
                processing.Add(edit);
            }
        }

        if (settled.Any(item => item.Outcome.Status == ScriptEditStatus.Failed))
        {
            // Site 9 (plan 011): deferred while an ask exchange runs.
            var eventId = $"edit-failed-{settled.First(item => item.Outcome.Status == ScriptEditStatus.Failed).Edit.Id}";
            EmitOrDefer("edit failed", PromptBuilder.ScriptEditFailedInstruction(), eventId);
        }

        // 3. Replay or release the current slide.
        if (TalkRunning && !_wrappingUp)
        {
            var released = wasHeld && !NarrationHeld;
            if ((currentChanged || released) && !NarrationHeld)
            {
                if (_exchange is { } exchange)
                {
                    // Site 10 (plan 011): the exchange owns the replay in every phase; it runs once when it ends.
                    exchange.ReplayDue = true;
                    exchange.ReplayChanged |= currentChanged;
                }
                else if (_state == PresenterState.Presenting)
                {
                    ReplayCurrentSlide(currentChanged);
                }
                else
                {
                    _replayOnResume = true;
                    _replayOnResumeChanged |= currentChanged;
                }
            }
        }

        // 4. Version first, so no "applied" frame precedes the narration it describes.
        if (versionMoved && _state != PresenterState.Connecting) EmitScriptVersion();

        // 5. Progress and terminal frames, once per id.
        foreach (var edit in processing)
        {
            LogMessage("info", $"edit: processing {edit.Id} on v{_scriptVersion}");
            ScriptEdit?.Invoke(new PresenterScriptEdit(edit.Id, ScriptEditStatus.Processing, edit.Targets, null, null, null));
        }

        foreach (var (edit, outcome) in settled)
        {
            if (outcome.Status == ScriptEditStatus.Applied)
            {
                var seconds = _timeProvider.GetElapsedTime(edit.QueuedAt).TotalSeconds;
                LogMessage("info", $"edit: applied {edit.Id} → v{outcome.Version} (slide {SlideList(edit.Targets)}) in {seconds:0.0} s");
                ScriptEdit?.Invoke(new PresenterScriptEdit(edit.Id, ScriptEditStatus.Applied, edit.Targets,
                    outcome.Version, outcome.Summary, null));
            }
            else
            {
                LogMessage("info", $"edit: failed {edit.Id} ({outcome.Error ?? "unknown"})");
                ScriptEdit?.Invoke(new PresenterScriptEdit(edit.Id, ScriptEditStatus.Failed, edit.Targets, null, null,
                    outcome.Error ?? ScriptEditErrors.Cancelled));
            }
        }

        if (settled.Count > 0 || processing.Count > 0) RecordActivity();
        if (!HasUnsettledEdits()) StopEditKeepAlive();
    }

    /// <summary>Presents the current slide from its start with the current text (after an edit or a revert).</summary>
    private void ReplayCurrentSlide(bool changed)
    {
        if (_presentation is null) return;
        Flush?.Invoke();
        if (changed)
        {
            var slide = _presentation.Slides[_slideIndex];
            _session?.AppendInstructions(PromptBuilder.ScriptUpdatedInstruction(_slideIndex, SlideCount, slide.Title),
                $"slide-{_slideIndex + 1}-updated-v{_scriptVersion}");
            LogMessage("info", $"edit: replay slide {_slideIndex + 1} (v{_scriptVersion})");
        }

        PresentSlide(_slideIndex, interrupt: true);
    }

    /// <summary>
    /// For <c>ResumeCore</c>: true when the resume must not send the stale resume instruction and nudge — either the slide
    /// is held (the reconcile that settles the edit replays it), or a paused replay is due (performed here).
    /// </summary>
    private bool TrainingOverridesResume()
    {
        if (NarrationHeld)
        {
            LogMessage("info", $"edit: resume while slide {_slideIndex + 1} is held");
            return true;
        }

        if (_replayOnResume && !_wrappingUp)
        {
            var changed = _replayOnResumeChanged;
            _replayOnResume = false;
            _replayOnResumeChanged = false;
            ReplayCurrentSlide(changed);
            return true;
        }

        return false;
    }

    private void EmitScriptVersion()
    {
        if (_presentation is null || _talkId is null) return;
        var version = new PresenterScriptVersion(_presentation.Id, _scriptVersion, _trainerMode, TrainerAvailable, VoiceTraining);
        Volatile.Write(ref _scriptVersionSnapshot, version);
        ScriptVersion?.Invoke(version);
        PublishTrainerState();
    }

    /// <summary>
    /// Publishes the server-authoritative Trainer mode (<c>trainer_state</c>): the running talk's mode, else the idle
    /// request that the next Start of its owner will honour. Every change of <see cref="_trainerMode"/>, of
    /// <see cref="_idleTrainerToggle"/> or of voice availability reaches the client through here, so the switch never
    /// shows a value the server does not hold. <paramref name="always"/> re-announces an unchanged state (a refusal).
    /// </summary>
    private void PublishTrainerState(bool always = false)
    {
        var available = TrainerAvailable;
        var state = _talkId is not null
            ? new PresenterTrainerState(_ownerId, _trainerMode, available, VoiceTraining)
            : _idleTrainerToggle is { } idle
                ? new PresenterTrainerState(idle.OwnerId, idle.On && available, available, true)
                : new PresenterTrainerState(null, false, available, true);
        if (!always && state == CurrentTrainerState()) return;
        Volatile.Write(ref _trainerStateSnapshot, state);
        TrainerState?.Invoke(state);
    }

    private bool HasUnsettledEdits() => _localEdits.Values.Any(edit => !edit.Settled);

    private void ArmEditKeepAlive()
    {
        if (_editKeepAlive is not null) return;
        var generation = ++_editKeepAliveGeneration;
        _editKeepAlive = _timeProvider.CreateTimer(
            _ => QueueFromProducer(new EditKeepAliveElapsed(generation)), null,
            TimeSpan.FromMilliseconds(EditKeepAliveMs), Timeout.InfiniteTimeSpan);
    }

    private void StopEditKeepAlive()
    {
        _editKeepAliveGeneration++;
        _editKeepAlive?.Dispose();
        _editKeepAlive = null;
    }

    // ---- Transcript context -----------------------------------------------------------------------------------------

    /// <summary>Keeps the last role-contiguous transcript turns (context for the reviser, never intent).</summary>
    private void RecordRecentTurn(string role, string delta)
    {
        if (_scriptRevisions is null || string.IsNullOrEmpty(delta) || role is not ("user" or "assistant")) return;
        if (_recentTurns.Count == 0 || _recentTurns[^1].Role != role)
        {
            if (string.IsNullOrWhiteSpace(delta)) return;
            _recentTurns.Add(new RecentTranscriptTurn(role, _slideIndex));
            if (_recentTurns.Count > MaxRecentTurns) _recentTurns.RemoveAt(0);
        }

        var text = _recentTurns[^1].Text;
        var room = MaxRecentTurnChars - text.Length;
        if (room > 0) text.Append(delta.Length <= room ? delta : delta[..room]);
    }

    private static string SlideList(IReadOnlyList<int> indexes) => string.Join(",", indexes.Select(i => i + 1));

    private sealed record EditIntent(
        string TalkId,
        string PresentationId,
        string OwnerId,
        IReadOnlyList<int> Targets,
        int BaseVersion,
        string Feedback,
        TrainingExchange? Exchange);

    private sealed class LocalEdit(string id, IReadOnlyList<int> targets, long queuedAt)
    {
        public string Id { get; } = id;
        public IReadOnlyList<int> Targets { get; } = targets;
        public long QueuedAt { get; } = queuedAt;
        public bool Settled { get; set; }
        public bool ProcessingEmitted { get; set; }
    }

    private sealed class RecentTranscriptTurn(string role, int slideIndex)
    {
        public string Role { get; } = role;
        public int SlideIndex { get; } = slideIndex;
        public System.Text.StringBuilder Text { get; } = new();
    }

    private abstract record TrainingEvent : PresenterEvent;
    private sealed record ReconcileRequested(string PresentationId) : TrainingEvent;
    private sealed record EditKeepAliveElapsed(long Generation) : TrainingEvent;
    private sealed record TrainerModeCommand(string OwnerId, bool On) : Command;
    private sealed record TrainOnTurnCommand(string OwnerId, string Question, string Answer, int SlideIndex) : Command;
}
