using PresenterAi.Application.Presenting.Asking;
using PresenterAi.Application.Presenting.VoiceCommands;

namespace PresenterAi.Application.Presenting;

/// <summary>
/// Plan 011 press-to-ask: one loop-only ask exchange (<see cref="_exchange"/>) from Ask start to the check-in's resolution.
/// Every source that can make the model speak or act, or move narration, consults it through
/// <see cref="ExchangeAllows"/> or <see cref="DeferUntilExchangeEnds"/>; <see cref="EndExchange"/> is its only exit.
/// Everything here runs on the presenter loop; timers and session callbacks re-enter through
/// <see cref="QueueFromProducer"/> with a loop-only generation or session check. No lock, no blocking wait.
/// </summary>
public sealed partial class Presenter
{
    public const int AskQuietTimeoutMs = 90_000;
    public const int AskTickMs = 1_000;
    public const int AskUnmuteAckTimeoutMs = 2_000;
    public const int AskLeadInMs = 200;
    /// <summary>P-11: from the T1 suite (longest Ask-done → first answer audio about 9.7 s at 24 s of kept speech).</summary>
    public const int AnswerStartBudgetMs = 15_000;
    /// <summary>P-11: re-arms never extend past AwaitingAnswer start + budget + this, even while work is outstanding.</summary>
    public const int AnswerCeilingExtraMs = 60_000;
    /// <summary>P-13: at check-in a user delta opens a new utterance only after this much transcript quiet.</summary>
    public const int CheckInQuietMs = 1_500;
    /// <summary>
    /// Owner decision "hold for speech" (2026-09-24): live input transcription lags speech by about 2.5 s, so after the
    /// mic last heard the listener the check-in waits this long for the reply's transcript before timing out.
    /// </summary>
    public const int CheckInTranscriptGraceMs = 3_000;
    /// <summary>The speech hold never keeps a check-in open longer than this past its normal reply window.</summary>
    public const int CheckInMaxHoldMs = 10_000;
    /// <summary>§4.1 transcriber port: end of utterance by update time, as the existing assembler does.</summary>
    public const int AskPhraseDebounceMs = 700;
    /// <summary>
    /// T8 regression fix (2026-09-24): model audio voiced within this long before Ask start (or at all while listening)
    /// makes the ask a residual candidate; an open residual is held until a voiced gap this long (the answer's 700 ms quiet).
    /// </summary>
    public const int ResidualGapMs = 700;
    /// <summary>
    /// A candidate's first voiced frame earlier than this after the send is the rest of the pre-Ask response: residual
    /// first frames came 64–205 ms after the send, genuine answers never before 1,273 ms (the burst is ingested first).
    /// </summary>
    public const int ResidualStartMs = 1_000;
    /// <summary>T8 regression fix: after a <c>limit_sent</c> burst without an answer, the cut-off nudge waits this long.</summary>
    public const int CutOffNudgeMs = 3_000;
    /// <summary>A user delta of the cut-off burst's transcript keeps the nudge at least this far away.</summary>
    public const int CutOffQuietMs = 2_000;
    public const string AskBusyMessage = "busy: the audience is asking a question";

    private readonly IAskTranscriber _askTranscriber;
    private AskExchange? _exchange;
    private int _askSequence;
    private ITimer? _askTickTimer;
    private long _askTickGeneration;
    private ITimer? _askAckTimer;
    private long _askAckGeneration;
    private ITimer? _askBudgetTimer;
    private long _askBudgetGeneration;
    private ITimer? _askPhraseTimer;
    private long _askPhraseGeneration;
    private ITimer? _askCutOffTimer;
    private long _askCutOffGeneration;
    // Arrival (loop time) of the last voiced model frame of the current session, forwarded or not.
    private long? _modelVoicedAt;
    // P-13 after the exchange: the last user delta of an ended exchange; deltas chained within CheckInQuietMs of it are
    // still the old question's trail and stay UI-only.
    private long? _askTrailingDeltaAt;
    // Hold for speech: a user delta before this (loop time) is a check-in reply that arrived after its exchange ended.
    private long? _askLateReplyUntil;
    private long _resetGeneration;
    private long _confirmationGeneration;
    private PendingReset? _pendingReset;
    // Review r1 #1: every presenter unmute and every ack of the current session is counted, so an ack without an
    // echoed id is attributed FIFO; an ack with one must match the ask's own unmute id.
    private ILiveSession? _unmuteLedgerSession;
    private long _unmutesSent;
    private long _unmuteAcks;
    // Review r1 #2: deferred notices whose exchange ended while the upstream was reset; appended once after reconnect.
    private readonly List<ModelNotice> _noticesForReconnect = [];

    /// <summary>Press-to-ask state for the <c>ask_state</c> frame (plan 011 §4.3); raised on the loop.</summary>
    public event Action<PresenterAskState>? AskState;

    /// <summary>The transcriber the ask exchange uses (plan 011 G1-5); <see cref="DisabledAskTranscriber"/> by default.</summary>
    public IAskTranscriber AskTranscriber => _askTranscriber;

    public Task<bool> AskStartAsync(CancellationToken cancellationToken = default) =>
        EnqueueCommandAsync(new AskStartCommand(), cancellationToken);

    public Task<bool> AskDoneAsync(CancellationToken cancellationToken = default) =>
        EnqueueCommandAsync(new AskDoneCommand(), cancellationToken);

    public Task<bool> AskExtendAsync(CancellationToken cancellationToken = default) =>
        EnqueueCommandAsync(new AskExtendCommand(), cancellationToken);

    public Task<bool> AskCancelAsync(CancellationToken cancellationToken = default) =>
        EnqueueCommandAsync(new AskCancelCommand(), cancellationToken);

    // ---- The one consult point ------------------------------------------------------------------------------------

    /// <summary>
    /// §4.1 design rule 2: whether a model-facing or narration-moving action may run now. True outside an exchange.
    /// While listening (incl. the unmute-ack wait) nothing the upstream requests is acted on; in the answer phases the
    /// answer may use tools and delegations, but no hold release, nudge, hold entry or wrap-up end runs.
    /// </summary>
    private bool ExchangeAllows(ModelAction action)
    {
        if (_exchange is not { } exchange) return true;
        var listening = exchange.Phase is AskPhase.Listening or AskPhase.Sending;
        return action switch
        {
            ModelAction.ReleaseQuestionHold or ModelAction.Nudge or ModelAction.EnterHold or ModelAction.WrapUpEnd => false,
            _ => !listening
        };
    }

    /// <summary>§4.1 design rule 2: queues a model-facing notice to run exactly once when the exchange ends; false outside one.</summary>
    private bool DeferUntilExchangeEnds(ModelNotice notice)
    {
        if (_exchange is not { } exchange) return false;
        exchange.Deferred.Add(notice);
        return true;
    }

    /// <summary>
    /// Appends a model-facing instruction now, or keeps it (as content, not bound to a session) until the exchange ends.
    /// </summary>
    private void EmitOrDefer(string name, string content, string eventId)
    {
        var notice = new ModelNotice(name, content, eventId);
        if (!DeferUntilExchangeEnds(notice)) _session?.AppendInstructions(notice.Content, notice.EventId);
    }

    /// <summary>
    /// Appends deferred notices once. With the upstream reset (send failure) they wait for the reconnect instead of
    /// being appended to no session (review r1 #2).
    /// </summary>
    private void DeliverNotices(IReadOnlyList<ModelNotice> notices)
    {
        if (notices.Count == 0) return;
        if (_session is { } session)
        {
            foreach (var notice in notices) session.AppendInstructions(notice.Content, notice.EventId);
            return;
        }

        _noticesForReconnect.AddRange(notices);
        LogMessage("info", $"ask: {notices.Count} deferred notices wait for the reconnect");
    }

    /// <summary>Called once a reconnect attached a new session: the notices kept over the reset are appended once.</summary>
    private void DeliverNoticesAfterReconnect()
    {
        if (_noticesForReconnect.Count == 0 || _session is not { } session) return;
        var notices = _noticesForReconnect.ToArray();
        _noticesForReconnect.Clear();
        foreach (var notice in notices) session.AppendInstructions(notice.Content, notice.EventId);
        LogMessage("info", $"ask: {notices.Length} deferred notices appended after the reconnect");
    }

    /// <summary>Every presenter unmute goes through here, so its ack can be attributed (review r1 #1).</summary>
    private bool SendUnmute(ILiveSession session, out string? eventId, out long ordinal)
    {
        SyncUnmuteLedger(session);
        var sent = session.Unmute(out eventId);
        ordinal = sent ? ++_unmutesSent : 0;
        return sent;
    }

    private void SyncUnmuteLedger(ILiveSession session)
    {
        if (ReferenceEquals(_unmuteLedgerSession, session)) return;
        _unmuteLedgerSession = session;
        _unmutesSent = 0;
        _unmuteAcks = 0;
    }

    /// <summary>Disposes the ask's transcription exactly once: ownership moves out of the exchange (review r1 #5).</summary>
    private static void ReleaseTranscription(AskExchange exchange)
    {
        var transcription = exchange.Transcription;
        exchange.Transcription = null;
        transcription?.Dispose();
    }

    // ---- Commands -------------------------------------------------------------------------------------------------

    private async Task<bool> AskStartCore()
    {
        if (_exchange is { Phase: AskPhase.Listening } listening)
        {
            EmitListening(listening);
            return true;
        }

        // Owner decision (review r1 #4): Ask during the answer is a follow-up question in the same exchange; it keeps
        // the resume point of the first ask. It is known before every refusal check, so each refusal goes through
        // RefuseStart, which keeps a surviving answer announced or ends it when its upstream is gone (review r2 #3).
        var followUp = _exchange is { Phase: AskPhase.AwaitingAnswer or AskPhase.Answering or AskPhase.CheckIn } ? _exchange : null;

        if (_state is not (PresenterState.Presenting or PresenterState.Paused))
        {
            RefuseStart(followUp, "refused_not_live");
            return false;
        }

        if (_muted)
        {
            LogMessage("info", "ask: refused while muted");
            RefuseStart(followUp, "refused_muted");
            return false;
        }

        // Only an Ask during the unmute-ack wait ends that exchange first (Stay).
        if (_exchange is not null && followUp is null) EndExchange(AskOutcome.Stay, "paused");

        if (_suspended)
        {
            await ReconnectAsync().ConfigureAwait(false);
            // A failed reconnect has already ended the talk.
            if (_state is not (PresenterState.Presenting or PresenterState.Paused) || _session is null || _suspended)
            {
                RefuseStart(followUp, "refused_not_live");
                return false;
            }
        }

        if (_session is not { } session)
        {
            RefuseStart(followUp, "refused_not_live");
            return false;
        }

        // From here to the end of the handler there is no await. A GuardElapsed already queued behind this command
        // carries the old generation and is dropped.
        _guard?.StopPauseGrace();
        if (!session.Mute())
        {
            if (_state == PresenterState.Paused) _guard?.Pause();
            LogMessage("warn", "ask: upstream mute refused; not asking");
            RefuseStart(followUp, "unavailable");
            return false;
        }

        var askId = $"ask_{++_askSequence}";
        IAskTranscription? transcription;
        try
        {
            transcription = _askTranscriber.Begin(askId);
        }
        catch (Exception exception)
        {
            LogMessage("warn", $"ask: transcriber failed to begin ({exception.GetType().Name})");
            if (!SendUnmute(session, out _, out _)) ResetUpstream();
            else if (_state == PresenterState.Paused) _guard?.Pause();
            RefuseStart(followUp, "unavailable");
            return false;
        }

        if (transcription is not null)
        {
            transcription.Updated += update => QueueFromProducer(new AskTranscriptChanged(askId, update));
        }

        var from = followUp is null ? StateName(_state) : "answer, follow-up";
        AskExchange exchange;
        if (followUp is not null)
        {
            // The same exchange listens again: deferred notices, replay-due and the resume point carry over.
            exchange = followUp;
            exchange.BeginFollowUp(askId, new AskRecorder(), transcription, _timeProvider.GetTimestamp());
            StopAskBudget();
            StopAskCutOff();
        }
        else
        {
            exchange = new AskExchange(askId, new AskRecorder(), transcription, _timeProvider.GetTimestamp(), _heardOutput)
            {
                ReplayDue = _replayOnResume,
                ReplayChanged = _replayOnResumeChanged
            };
            _replayOnResume = false;
            _replayOnResumeChanged = false;
            _exchange = exchange;
        }

        MarkResidualCandidate(exchange);

        _askTrailingDeltaAt = null;
        _askLateReplyUntil = null;
        ResetUtterance();
        // P-9: work started before Ask is abandoned, as navigation does: no result is submitted and no continue sent.
        _runGeneration++;
        _toolRoundTracker.Clear();
        _approvedTools.Clear();
        _navigatingCallIds.Clear();
        if (_state == PresenterState.Presenting)
        {
            PauseCore();
        }
        else
        {
            ClearQuestionHold();
            ClosePermit();
        }

        // PauseCore re-armed the grace; the ask is a pause without one.
        _guard?.StopPauseGrace();
        ArmAskTick();
        LogMessage("info", $"ask: listening (from {from})");
        EmitListening(exchange);
        return true;
    }

    /// <summary>
    /// The one refusal path of Ask start. A plain refused start reports <c>off</c>. A refused follow-up keeps its answer
    /// running and re-announces <c>answering</c> while its upstream is live; when the upstream is gone (a refused
    /// rollback unmute reset it, or the talk ended) the answer exchange is ended once instead, with its deferred
    /// notices kept for the reconnect and its replay due kept for the next resume (review r2 #2, #3).
    /// </summary>
    private void RefuseStart(AskExchange? followUp, string reason)
    {
        if (followUp is null || !ReferenceEquals(_exchange, followUp))
        {
            EmitAskOff(null, reason);
            return;
        }

        if (_session is null || _suspended || _state is not (PresenterState.Presenting or PresenterState.Paused))
        {
            LogMessage("warn", $"ask: follow-up refused ({reason}) with the upstream gone; the answer ends");
            EndExchange(_state is PresenterState.Presenting or PresenterState.Paused ? AskOutcome.Stay : AskOutcome.Ended, reason);
            return;
        }

        EmitAskOff(null, reason);
        EmitAnswering(followUp);
    }

    private bool AskDoneCore(string reason)
    {
        if (_exchange is not { Phase: AskPhase.Listening } exchange) return false;
        StopAskTick();
        StopAskPhrase();
        ReleaseTranscription(exchange);
        if (!exchange.Recorder.HasSpeech)
        {
            EndExchange(AskOutcome.Stay, "empty");
            return false;
        }

        exchange.Chunks = exchange.Recorder.Complete();
        exchange.SendReason = reason;
        exchange.SentElapsedMs = ElapsedMs(exchange.StartedAt);
        exchange.Phase = AskPhase.Sending;
        exchange.AwaitingUnmuteAck = true;
        if (_session is not { } session || !SendUnmute(session, out var unmuteId, out var unmuteOrdinal))
        {
            FailSend(0, exchange.Chunks.Count + 1);
            return false;
        }

        exchange.UnmuteEventId = unmuteId;
        exchange.UnmuteOrdinal = unmuteOrdinal;
        exchange.UpstreamMuted = false;
        // P-18: the loop is free while the upstream acknowledges the unmute; the first of the ack or the timeout sends.
        var generation = ++_askAckGeneration;
        _askAckTimer?.Dispose();
        _askAckTimer = _timeProvider.CreateTimer(_ => QueueFromProducer(new UnmuteAckTimedOut(generation)), null,
            TimeSpan.FromMilliseconds(AskUnmuteAckTimeoutMs), Timeout.InfiniteTimeSpan);
        return true;
    }

    private bool AskExtendCore()
    {
        if (_exchange is not { Phase: AskPhase.Listening } exchange) return false;
        exchange.LastExtendAt = _timeProvider.GetTimestamp();
        LogMessage("info", "ask: extended");
        EmitListening(exchange);
        return true;
    }

    private bool AskCancelCore()
    {
        if (_exchange is not ({ Phase: AskPhase.Listening } or { Phase: AskPhase.Sending, AwaitingUnmuteAck: true })) return false;
        EndExchange(AskOutcome.Stay, "cancelled");
        return true;
    }

    /// <summary>
    /// The command table of §4.1 for a command that arrives while an exchange runs. Returns true when the command is
    /// fully handled here (<paramref name="result"/> is its result); false when it runs as usual afterwards.
    /// </summary>
    private bool TryHandleCommandDuringExchange(Command command, out bool result)
    {
        result = false;
        if (_exchange is not { } exchange) return false;
        var listening = exchange.Phase is AskPhase.Listening or AskPhase.Sending;
        if (command.InvocationCallId is not null &&
            command is NextCommand or PrevCommand or GotoCommand or PauseCommand or ResumeCommand or ConfirmEndCommand &&
            !ExchangeAllows(ModelAction.ToolCommand))
        {
            LogMessage("info", "ask: tool command refused");
            return true;
        }

        switch (command)
        {
            case PauseCommand when listening:
                return true;
            case PauseCommand or ConfirmEndCommand:
                EndExchange(AskOutcome.Stay, "paused");
                return false;
            case ResumeCommand when listening:
                EndExchange(AskOutcome.Stay, "resumed");
                return false;
            case ResumeCommand:
                // Continue (P-17): skips the check-in and resumes from the interrupted sentence.
                EndExchange(AskOutcome.Resume, "continued", "ask: continue; resuming");
                result = true;
                return true;
            case NextCommand or PrevCommand or GotoCommand:
                EndExchange(AskOutcome.Navigated, "navigated");
                return false;
            case MuteCommand:
                _muted = true;
                if (listening)
                {
                    // The upstream is already muted; ending the exchange must not unmute it.
                    EndExchange(AskOutcome.Stay, "muted");
                }
                else
                {
                    // P-16: the mic is no longer forwarded, but the upstream Mute() waits for the exchange end so the
                    // input clock keeps running through the answer.
                    LogMessage("info", "ask: mute deferred to the exchange end");
                }

                PublishSnapshot();
                result = true;
                return true;
            default:
                return false;
        }
    }

    // ---- Recording and the burst ----------------------------------------------------------------------------------

    /// <summary>Mic PCM while listening goes to the recorder, never upstream; during the unmute-ack wait it is dropped.</summary>
    private bool RecordAskAudio(AskExchange exchange, byte[] pcm16)
    {
        if (exchange.Phase != AskPhase.Listening) return false;
        var voicedBefore = exchange.Recorder.Stats.VoicedMs;
        var full = exchange.Recorder.Append(pcm16);
        exchange.Transcription?.Append(pcm16);
        if (exchange.Recorder.Stats.VoicedMs > voicedBefore) exchange.LastVoicedAt = _timeProvider.GetTimestamp();
        if (full)
        {
            LogMessage("info", $"ask: speech limit of {AskRecorder.MaxRetainedMs / 1000} s reached");
            AskDoneCore("limit_sent");
        }

        return true;
    }

    /// <summary>
    /// An upstream unmute ack. Only the ack of this ask's own unmute sends the burst (review r1 #1): an echoed
    /// <c>client_event_id</c> must equal the id this ask's unmute sent; an ack without one is attributed FIFO over every
    /// unmute the presenter sent on this session, so an older ask's (or a user unmute's) late ack is consumed as debt.
    /// </summary>
    private void OnUnmuteAcked(ILiveSession session, string? clientEventId)
    {
        if (!ReferenceEquals(session, _session)) return;
        SyncUnmuteLedger(session);
        var ordinal = ++_unmuteAcks;
        if (_exchange is not { Phase: AskPhase.Sending, AwaitingUnmuteAck: true } exchange) return;
        var own = clientEventId is not null && exchange.UnmuteEventId is not null
            ? clientEventId == exchange.UnmuteEventId
            : ordinal >= exchange.UnmuteOrdinal;
        if (!own)
        {
            LogMessage("info", "ask: unmute ack of an earlier unmute ignored");
            return;
        }

        SendBurst(exchange);
    }

    private void OnUnmuteAckTimedOut(long generation)
    {
        if (generation != _askAckGeneration || _exchange is not { Phase: AskPhase.Sending, AwaitingUnmuteAck: true } exchange) return;
        LogMessage("warn", $"ask: unmute ack timed out after {AskUnmuteAckTimeoutMs} ms; sending");
        SendBurst(exchange);
    }

    /// <summary>Queues the 200 ms zero lead-in and every chunk unpaced (P-1, P-18); true from SendAudio means queued only.</summary>
    private void SendBurst(AskExchange exchange)
    {
        exchange.AwaitingUnmuteAck = false;
        StopAskAck();
        var chunks = exchange.Chunks!;
        exchange.Chunks = null;
        var total = chunks.Count + 1;
        if (_session is not { } session || !session.SendAudio(new byte[AskLeadInMs * AskRecorder.BytesPerMs]))
        {
            FailSend(1, total);
            return;
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            if (!session.SendAudio(chunks[i]))
            {
                FailSend(i + 2, total);
                return;
            }
        }

        var stats = exchange.Recorder.Stats;
        LogMessage("info", $"ask: sent ({exchange.SendReason}) — recorded {Seconds(stats.RecordedMs)} s, kept " +
            $"{Seconds(stats.KeptMs)} s (voiced {Seconds(stats.VoicedMs)} s) + {Seconds(stats.TailMs)} s tail, " +
            $"{chunks.Count} chunks; rms bands {stats.Bands}");

        SetState(PresenterState.Presenting);
        _guard?.StartPresenting();
        SetInteraction(Interaction.None);
        ClearSilenceTimer();
        ClearWrapUpTimer();
        ClearNudgeTimer();
        _questionHoldOpen = true;
        _answerVoiced = false;
        // Answer detection ignores upstream timestamps (§9.5): any voiced answer audio counts.
        _latestQuestionEndMs = null;
        _questionOpenedAt = _timeProvider.GetTimestamp();
        exchange.Phase = AskPhase.AwaitingAnswer;
        exchange.CheckInBegun = false;
        exchange.AwaitingAnswerSince = _timeProvider.GetTimestamp();
        ArmAnswerWait();
        if (exchange.SendReason == "limit_sent") ArmAskCutOff(CutOffNudgeMs);
        EmitAnswering(exchange);
    }

    // ---- Residual stream (T8 regression fix, 2026-09-24) ----------------------------------------------------------

    /// <summary>
    /// Work begun before Ask cannot be cancelled upstream (§9.7): the response running at Ask start keeps generating
    /// while muted, or is held and released after the unmute (T8 row 4); either way the rest of it arrives first after
    /// the burst. Arrival times only (§9.5). An ask is a candidate when the model was voiced within
    /// <see cref="ResidualGapMs"/> before Ask start or at all while listening. A candidate's first voiced frame after the
    /// send decides: earlier than <see cref="ResidualStartMs"/> it opens the residual, later it is the answer. An open
    /// residual closes at the first voiced frame at least <see cref="ResidualGapMs"/> after the previous post-send voiced
    /// frame, which is the answer. Returns true when the frame is dropped: residual audio is neither forwarded nor taken
    /// as the answer. Unvoiced frames of an open residual, and a candidate's unvoiced frames before its first voiced one
    /// within <see cref="ResidualStartMs"/>, are dropped too, as every frame is while listening: forwarding the stream's
    /// silence would queue playback and stretch the answer's playback estimate, and no answer arrives that early.
    /// </summary>
    private bool HoldResidualAudio(bool voiced, int bytes)
    {
        var now = _timeProvider.GetTimestamp();
        if (voiced) _modelVoicedAt = now;
        if (_exchange is not { } exchange) return false;
        if (exchange.Phase is AskPhase.Listening or AskPhase.Sending)
        {
            // The forwarding gate drops the frame; a voiced one makes the ask a candidate.
            if (voiced) exchange.ResidualCandidate = true;
            return false;
        }

        if (exchange.Phase != AskPhase.AwaitingAnswer || !exchange.ResidualCandidate) return false;
        if (!exchange.ResidualDecided)
        {
            if (_timeProvider.GetElapsedTime(exchange.AwaitingAnswerSince, now).TotalMilliseconds >= ResidualStartMs)
            {
                exchange.ResidualDecided = true;
                return false;
            }

            if (!voiced) return true;
            exchange.ResidualDecided = true;
            exchange.ResidualOpen = true;
            exchange.ResidualVoicedAt = now;
            exchange.ResidualDroppedMs += bytes / AskRecorder.BytesPerMs;
            LogMessage("info", "ask: residual model audio at send; held until a gap");
            return true;
        }

        if (!exchange.ResidualOpen) return false;
        if (voiced && exchange.ResidualVoicedAt is { } last &&
            _timeProvider.GetElapsedTime(last, now).TotalMilliseconds >= ResidualGapMs)
        {
            exchange.ResidualOpen = false;
            LogMessage("info", $"ask: residual model audio ended; dropped {exchange.ResidualDroppedMs} ms");
            return false;
        }

        if (voiced) exchange.ResidualVoicedAt = now;
        exchange.ResidualDroppedMs += bytes / AskRecorder.BytesPerMs;
        return true;
    }

    /// <summary>At every Ask start (first or follow-up): model audio voiced just before it makes the ask a candidate.</summary>
    private void MarkResidualCandidate(AskExchange exchange) =>
        exchange.ResidualCandidate = _modelVoicedAt is { } voicedAt && ElapsedMs(voicedAt) < ResidualGapMs;

    // ---- Cut-off nudge at the cap (T8 regression fix, 2026-09-24) -------------------------------------------------

    /// <summary>
    /// A burst sent at the speech cap ends mid-sentence and the model waits for the rest of the utterance. After
    /// <see cref="CutOffNudgeMs"/> without an answer the model is asked once to answer what it heard.
    /// </summary>
    private void ArmAskCutOff(int milliseconds)
    {
        StopAskCutOff();
        if (_exchange is not { } exchange) return;
        exchange.CutOffDueAt = _timeProvider.GetTimestamp() + MsToTimestamp(milliseconds);
        var generation = _askCutOffGeneration;
        _askCutOffTimer = _timeProvider.CreateTimer(_ => QueueFromProducer(new AskCutOffElapsed(generation)), null,
            TimeSpan.FromMilliseconds(milliseconds), Timeout.InfiniteTimeSpan);
    }

    /// <summary>A user delta of the cut-off burst's transcript: the nudge waits for <see cref="CutOffQuietMs"/> of quiet, never less.</summary>
    private void PostponeAskCutOff(AskExchange exchange)
    {
        if (_askCutOffTimer is null || exchange.Phase != AskPhase.AwaitingAnswer || exchange.CutOffDueAt is not { } due) return;
        var left = (int)Math.Ceiling(_timeProvider.GetElapsedTime(_timeProvider.GetTimestamp(), due).TotalMilliseconds);
        ArmAskCutOff(Math.Max(CutOffQuietMs, left));
    }

    private void OnAskCutOffElapsed(long generation)
    {
        if (generation != _askCutOffGeneration) return;
        _askCutOffTimer = null;
        if (_exchange is not { Phase: AskPhase.AwaitingAnswer, SendReason: "limit_sent", CutOffNudged: false } exchange ||
            _answerVoiced || _pendingTool is not null || _toolRoundTracker.HasPendingBackendDelegation) return;
        exchange.CutOffNudged = true;
        _session?.AppendInstructions(PromptBuilder.AskCutOffInstruction(), $"{exchange.Id}-cut-off");
        LogMessage("info", "ask: question cut off at the cap; asked the model to answer what it heard");
        // The nudge waits out the burst's transcript (about 10 s after a 25 s burst, T8 re-run): the model gets a fresh
        // answer budget from here, never past the ceiling.
        var since = _timeProvider.GetElapsedTime(exchange.AwaitingAnswerSince).TotalMilliseconds;
        ArmAskBudget(Math.Max(0, Math.Min(AnswerStartBudgetMs, AnswerStartBudgetMs + AnswerCeilingExtraMs - since)));
    }

    private void StopAskCutOff()
    {
        _askCutOffGeneration++;
        _askCutOffTimer?.Dispose();
        _askCutOffTimer = null;
    }

    /// <summary>P-12: a refused unmute or append ends the ask <c>send_failed</c> (never <c>sent</c>) and resets the upstream.</summary>
    private void FailSend(int chunk, int total)
    {
        LogMessage("warn", $"ask: send failed at chunk {chunk}/{total}; upstream reset");
        ResetUpstream();
        EndExchange(AskOutcome.Stay, "send_failed");
    }

    // ---- Answer wait (P-11) ---------------------------------------------------------------------------------------

    /// <summary>
    /// The one question-hold re-arm. Outside an exchange it is the generic 15 s hold; while listening nothing; in
    /// AwaitingAnswer the answer budget, never past its ceiling; in Answering/CheckIn the interaction timers own it.
    /// </summary>
    private void ArmAnswerWait()
    {
        if (_exchange is not { } exchange)
        {
            ArmQuestionHold();
            return;
        }

        if (exchange.Phase != AskPhase.AwaitingAnswer) return;
        var since = _timeProvider.GetElapsedTime(exchange.AwaitingAnswerSince).TotalMilliseconds;
        var wait = Math.Min(AnswerStartBudgetMs, AnswerStartBudgetMs + AnswerCeilingExtraMs - since);
        ArmAskBudget(Math.Max(0, wait));
    }

    private void OnAskBudgetElapsed(long generation)
    {
        if (generation != _askBudgetGeneration || _exchange is not { Phase: AskPhase.AwaitingAnswer } exchange) return;
        var since = _timeProvider.GetElapsedTime(exchange.AwaitingAnswerSince).TotalMilliseconds;
        var ceiling = AnswerStartBudgetMs + AnswerCeilingExtraMs;
        if (_toolRoundTracker.HasPendingBackendDelegation && since < ceiling)
        {
            LogMessage("info", "ask: answer work outstanding; waiting until the ceiling");
            ArmAskBudget(ceiling - since);
            return;
        }

        // T8 regression fix: the response begun before Ask is still streaming; the answer can only follow it.
        if (exchange.ResidualOpen && exchange.ResidualVoicedAt is { } voicedAt && ElapsedMs(voicedAt) < ResidualGapMs &&
            since < ceiling)
        {
            LogMessage("info", "ask: residual model audio still arriving; waiting longer for the answer");
            ArmAskBudget(Math.Min(AnswerStartBudgetMs, ceiling - since));
            return;
        }

        LogMessage("warn", $"ask: no answer within {since / 1000:0} s");
        EndExchange(AskOutcome.Resume, "continued", "ask: resuming without an answer");
    }

    private void ArmAskBudget(double milliseconds)
    {
        StopAskBudget();
        var generation = _askBudgetGeneration;
        _askBudgetTimer = _timeProvider.CreateTimer(_ => QueueFromProducer(new AskBudgetElapsed(generation)), null,
            TimeSpan.FromMilliseconds(milliseconds), Timeout.InfiniteTimeSpan);
    }

    /// <summary>Called when the first voiced answer audio enters, or re-enters, Answering (§4.1 phase table).</summary>
    private void MarkExchangeAnswering()
    {
        if (_exchange is not { Phase: AskPhase.AwaitingAnswer or AskPhase.CheckIn } exchange) return;
        exchange.Phase = AskPhase.Answering;
        StopAskBudget();
        StopAskCutOff();
    }

    /// <summary>
    /// A tool confirmation asked during the answer replaced the Answering interaction; once it settles, the answer's
    /// quiet timer takes over again so the check-in and its timeout still end the exchange.
    /// </summary>
    private void RestoreExchangeAnswerTimers()
    {
        if (_exchange is not { Phase: AskPhase.Answering or AskPhase.CheckIn } || !_answerVoiced) return;
        SetInteraction(Interaction.Answering);
        ArmInteraction(700);
    }

    /// <summary>
    /// The speech taken as the answer was filler (a delegation followed it): back to AwaitingAnswer. The budget keeps
    /// its original start, so the ceiling still bounds the wait; a check-in reply already open is dropped.
    /// </summary>
    private void ReturnExchangeToAwaitingAnswer()
    {
        if (_exchange is not { Phase: AskPhase.Answering or AskPhase.CheckIn } exchange) return;
        exchange.Phase = AskPhase.AwaitingAnswer;
        exchange.CheckInBegun = false;
        if (exchange.UtteranceOpen)
        {
            ResetUtterance();
            exchange.UtteranceOpen = false;
            exchange.UtteranceConfirmation = null;
        }
    }

    /// <summary>
    /// The existing 700 ms quiet after the answer moved to AwaitingCarryOn: the check-in begins (loop time). Voiced model
    /// audio after it (a blip, or a reaction to the user's reply before its transcript arrives) re-enters Answering and
    /// comes back here; that is the same check-in, so it is logged once.
    /// </summary>
    private void MarkExchangeCheckIn()
    {
        if (_exchange is not { } exchange || exchange.Phase is AskPhase.Listening or AskPhase.Sending) return;
        exchange.Phase = AskPhase.CheckIn;
        if (exchange.CheckInBegun) return;
        exchange.CheckInBegun = true;
        LogMessage("info", "ask: check-in");
    }

    // ---- Check-in reply window: hold for speech (owner decision, 2026-09-24) ---------------------------------------

    /// <summary>Model audio forwarded while the exchange waits for or plays the answer extends its playback estimate.</summary>
    private void TrackExchangePlayback(int bytes)
    {
        if (_exchange is not { Phase: AskPhase.AwaitingAnswer or AskPhase.Answering or AskPhase.CheckIn } exchange) return;
        var now = _timeProvider.GetTimestamp();
        var from = exchange.PlaybackEndsAt is { } end && end > now ? end : now;
        exchange.PlaybackEndsAt = from + MsToTimestamp(bytes / AskRecorder.BytesPerMs);
    }

    /// <summary>A mic frame after Ask done: voiced speech (the recorder's RMS threshold) holds the check-in window.</summary>
    private void TrackExchangeMic(ReadOnlySpan<byte> pcm16)
    {
        if (_exchange is not { Phase: AskPhase.AwaitingAnswer or AskPhase.Answering or AskPhase.CheckIn } exchange ||
            !AudioLevel.IsVoiced(pcm16)) return;
        exchange.LastMicVoicedAt = _timeProvider.GetTimestamp();
    }

    /// <summary>
    /// The check-in's reply window starts when its audio has finished playing, not at the 700 ms receive quiet while
    /// the model is still audible (audio arrives faster than it plays). Returns the timer delay for the window's end.
    /// </summary>
    private int CheckInWindowDelayMs(int receiveQuietDelayMs)
    {
        if (_exchange is not { } exchange) return receiveQuietDelayMs;
        var now = _timeProvider.GetTimestamp();
        var playing = exchange.PlaybackEndsAt is { } end && end > now
            ? (int)Math.Ceiling(_timeProvider.GetElapsedTime(now, end).TotalMilliseconds)
            : 0;
        var delay = playing > 0 ? Math.Max(receiveQuietDelayMs, playing + FollowUpWaitMs) : receiveQuietDelayMs;
        exchange.CheckInWindowStartsAt = now + MsToTimestamp(delay - FollowUpWaitMs);
        exchange.CheckInHeld = false;
        if (delay > receiveQuietDelayMs) LogMessage("info", $"ask: check-in window starts after playback (+{playing} ms)");
        return delay;
    }

    /// <summary>
    /// The check-in window elapsed. While the mic heard the listener within <see cref="CheckInTranscriptGraceMs"/>, or
    /// a reply is still being assembled, it stays open (bounded by <see cref="CheckInMaxHoldMs"/>). The hold never
    /// makes a delta count: P-13 still decides what opens a reply. Returns true when the window was held.
    /// </summary>
    private bool HoldCheckInForSpeech()
    {
        if (_exchange is not { } exchange) return false;
        var now = _timeProvider.GetTimestamp();
        var hold = exchange.UtteranceOpen ? AskPhraseDebounceMs + 50 : 0;
        if (exchange.LastMicVoicedAt is { } voiced)
            hold = Math.Max(hold, CheckInTranscriptGraceMs - (int)_timeProvider.GetElapsedTime(voiced, now).TotalMilliseconds);
        if (exchange.CheckInWindowStartsAt is { } start)
            hold = Math.Min(hold,
                FollowUpWaitMs + CheckInMaxHoldMs - (int)_timeProvider.GetElapsedTime(start, now).TotalMilliseconds);
        if (hold <= 0) return false;
        if (!exchange.CheckInHeld)
        {
            exchange.CheckInHeld = true;
            LogMessage("info", "ask: check-in held for listener speech");
        }

        ArmInteraction(hold);
        return true;
    }

    private long MsToTimestamp(int milliseconds) => _timeProvider.TimestampFrequency * milliseconds / 1000;

    // ---- Check-in turn-taking (P-13) ------------------------------------------------------------------------------

    /// <summary>
    /// A user delta while an exchange runs. Before CheckIn it is UI-only. Once the check-in has begun it opens a new
    /// utterance only after <see cref="CheckInQuietMs"/> without any user delta; every other delta stays UI-only and
    /// restarts that window. Model audio that re-enters Answering after the check-in began does not revoke it: the
    /// upstream hears the user's reply before its transcript arrives and may voice something first (T8 live run).
    /// No upstream timestamp is trusted.
    /// </summary>
    private void OnExchangeUserDelta(AskExchange exchange, TranscriptReceived transcript)
    {
        var now = _timeProvider.GetTimestamp();
        PostponeAskCutOff(exchange);
        var quiet = exchange.LastUserDeltaAt is not { } previous ||
            _timeProvider.GetElapsedTime(previous, now).TotalMilliseconds >= CheckInQuietMs;
        exchange.LastUserDeltaAt = now;
        if (exchange.UtteranceOpen)
        {
            AppendUtterance(transcript);
            return;
        }

        // Review r1 #3: a pending tool confirmation in an answer phase takes a spoken yes/no by the same rule.
        var confirming = _pendingTool is not null &&
            _interaction is Interaction.AwaitingConfirmQuestion or Interaction.AwaitingConfirmAnswer &&
            exchange.Phase is AskPhase.AwaitingAnswer or AskPhase.Answering or AskPhase.CheckIn;
        var checkIn = exchange.Phase == AskPhase.CheckIn ||
            (exchange.CheckInBegun && exchange.Phase == AskPhase.Answering);
        if ((checkIn || confirming) && quiet)
        {
            ResetUtterance();
            exchange.UtteranceOpen = true;
            // Review r2 #1: the purpose is fixed when the utterance's first delta arrives, never at its completion.
            exchange.UtteranceConfirmation = confirming ? _confirmationGeneration : null;
            AppendUtterance(transcript);
        }
    }

    /// <summary>
    /// A tool confirmation began (review r2 #1). An exchange utterance already open began before it, so it can neither
    /// approve nor decline it: it is discarded, and its remaining deltas fall inside the 1.5 s quiet window, so only a
    /// fresh utterance whose first delta follows the confirmation's start after transcript quiet can answer it.
    /// </summary>
    private void OnToolConfirmationStarted()
    {
        _confirmationGeneration++;
        if (_exchange is not { UtteranceOpen: true } exchange) return;
        ResetUtterance();
        exchange.UtteranceOpen = false;
        exchange.UtteranceConfirmation = null;
        LogMessage("info", "ask: open reply discarded; a tool confirmation began");
    }

    /// <summary>True when a user delta after an exchange is still the old question's trail (P-13); it stays UI-only.</summary>
    private bool IsTrailingAskDelta()
    {
        var now = _timeProvider.GetTimestamp();
        if (_askLateReplyUntil is { } until)
        {
            if (now < until)
            {
                LogMessage("info", "ask: late check-in reply after the exchange ended; ignored");
                return true;
            }

            _askLateReplyUntil = null;
        }

        if (_askTrailingDeltaAt is not { } trailing) return false;
        if (_timeProvider.GetElapsedTime(trailing, now).TotalMilliseconds < CheckInQuietMs)
        {
            _askTrailingDeltaAt = now;
            return true;
        }

        _askTrailingDeltaAt = null;
        return false;
    }

    /// <summary>
    /// A qualifying check-in utterance is complete. Its intent is filtered right after matching: only Yes, No, Resume and
    /// Pause act (barge-in allowed, so the "model speaking" filter does not apply); anything else is unclear and gets
    /// the existing follow-up once more.
    /// </summary>
    private void CompleteExchangeUtterance(AskExchange exchange)
    {
        var phrase = _utterance;
        var tooLong = _utteranceTooLong || _timeProvider.GetElapsedTime(_utteranceOpenedAt).TotalMilliseconds > 6000;
        ResetUtterance();
        exchange.UtteranceOpen = false;
        var confirmationReply = exchange.UtteranceConfirmation;
        exchange.UtteranceConfirmation = null;
        var command = tooLong ? null : VoiceCommandMatcher.Match(phrase);
        var confirmationPending = _pendingTool is not null &&
            _interaction is Interaction.AwaitingConfirmQuestion or Interaction.AwaitingConfirmAnswer;
        if (confirmationReply is not null && (!confirmationPending || confirmationReply != _confirmationGeneration))
        {
            LogMessage("info", "ask: reply to a settled confirmation ignored");
            return;
        }

        if (confirmationPending && confirmationReply is null)
        {
            // Began as a check-in reply, not as an answer to this confirmation (review r2 #1).
            LogMessage("info", "ask: check-in reply ignored while a tool confirmation is pending");
            return;
        }

        if (confirmationPending)
        {
            // Review r1 #3: only yes or no settle the confirmation; nothing else (navigation included) acts.
            switch (command?.Intent)
            {
                case VoiceCommandIntent.Yes:
                    LogMessage("info", "ask: tool confirmation answered yes");
                    ApproveToolConfirmation();
                    return;
                case VoiceCommandIntent.No:
                    LogMessage("info", "ask: tool confirmation answered no");
                    CancelToolConfirmation("declined");
                    return;
                default:
                    LogMessage("info", "ask: unclear confirmation reply; left to the timeout");
                    return;
            }
        }

        switch (command?.Intent)
        {
            case VoiceCommandIntent.Yes or VoiceCommandIntent.Resume:
                EndExchange(AskOutcome.Resume, "continued", "question: confirmed; resuming");
                return;
            case VoiceCommandIntent.No:
                LogMessage("info", "ask: check-in answered no; waiting on the slide");
                EndExchange(AskOutcome.Stay, "waiting");
                SetInteraction(Interaction.WaitingOnSlide);
                ClearQuestionTimer();
                ClearSilenceTimer();
                return;
            case VoiceCommandIntent.Pause:
                EndExchange(AskOutcome.Stay, "paused");
                PauseCore();
                return;
        }

        if (exchange.FollowUpUsed)
        {
            LogMessage("info", "ask: unclear check-in reply; left to the timeout");
            return;
        }

        exchange.FollowUpUsed = true;
        LogMessage("info", "ask: unclear check-in reply; follow-up");
        SetInteraction(Interaction.None);
        exchange.Phase = AskPhase.AwaitingAnswer;
        exchange.CheckInBegun = false;
        exchange.AwaitingAnswerSince = _timeProvider.GetTimestamp();
        OpenOrExtendQuestionHold(null);
    }

    // ---- Transcriber port (AC5) ----------------------------------------------------------------------------------

    /// <summary>
    /// An update of the current ask's transcription. Dropped unless the exchange is listening with that id and the
    /// revision is higher than any accepted one, so stale, reordered and duplicate updates change nothing. A final
    /// update is evaluated at once; otherwise a 700 ms debounce, re-armed on every accepted update, decides.
    /// </summary>
    private void OnAskTranscriptChanged(AskTranscriptChanged changed)
    {
        if (_exchange is not { Phase: AskPhase.Listening } exchange || exchange.Id != changed.AskId ||
            changed.Update.Revision <= exchange.LastRevision) return;
        exchange.LastRevision = changed.Update.Revision;
        exchange.LatestText = changed.Update.Text;
        StopAskPhrase();
        if (changed.Update.Final)
        {
            EvaluateAskPhrase(exchange);
            return;
        }

        var generation = _askPhraseGeneration;
        _askPhraseTimer = _timeProvider.CreateTimer(_ => QueueFromProducer(new AskPhraseElapsed(generation)), null,
            TimeSpan.FromMilliseconds(AskPhraseDebounceMs), Timeout.InfiniteTimeSpan);
    }

    private void OnAskPhraseElapsed(long generation)
    {
        if (generation != _askPhraseGeneration || _exchange is not { Phase: AskPhase.Listening } exchange) return;
        _askPhraseTimer = null;
        EvaluateAskPhrase(exchange);
    }

    /// <summary>An end-of-utterance "ask done" phrase finishes the ask; the stripped question stays on the exchange (P-7).</summary>
    private void EvaluateAskPhrase(AskExchange exchange)
    {
        if (AskDoneLexicon.Match(exchange.LatestText ?? string.Empty) is not { } match) return;
        exchange.Question = match.Question;
        LogMessage("info", $"ask: done by phrase ({match.Language}), question {match.Question.Length} chars");
        AskDoneCore("phrase_sent");
    }

    private void StopAskPhrase()
    {
        _askPhraseGeneration++;
        _askPhraseTimer?.Dispose();
        _askPhraseTimer = null;
    }

    // ---- Exchange end ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The only exit of an exchange: idempotent, exactly once. Clears <see cref="_exchange"/> first, then applies the
    /// deferred notices and the replay-due flag per the §4.1 outcome table and emits the one <c>off</c> frame.
    /// </summary>
    private void EndExchange(AskOutcome outcome, string reason, string? resumeMessage = null)
    {
        if (_exchange is not { } exchange) return;
        _exchange = null;
        StopAskTick();
        StopAskAck();
        StopAskBudget();
        StopAskPhrase();
        StopAskCutOff();
        ReleaseTranscription(exchange);
        exchange.Chunks = null;
        if (exchange.UtteranceOpen) ResetUtterance();
        var deferred = exchange.Deferred.ToArray();
        exchange.Deferred.Clear();
        var wasListening = exchange.Phase is AskPhase.Listening or AskPhase.Sending;
        _askTrailingDeltaAt = outcome is AskOutcome.Resume or AskOutcome.Stay ? exchange.LastUserDeltaAt : null;
        _askLateReplyUntil = outcome is AskOutcome.Resume or AskOutcome.Stay && exchange.CheckInBegun
            ? _timeProvider.GetTimestamp() + MsToTimestamp(CheckInTranscriptGraceMs)
            : null;

        if (outcome != AskOutcome.Ended && _session is { } session)
        {
            if (exchange.UpstreamMuted && !_muted)
            {
                // A refused unmute resets before the notices are delivered, so they wait for the reconnect.
                if (!SendUnmute(session, out _, out _)) ResetUpstream();
            }
            else if (!exchange.UpstreamMuted && _muted)
            {
                session.Mute();
            }
        }

        if (wasListening && outcome is AskOutcome.Stay or AskOutcome.Navigated && reason != "send_failed")
            LogMessage("info", $"ask: cancelled ({reason})");
        LogMessage("info", $"ask: exchange ended ({outcome.ToString().ToLowerInvariant()}), {deferred.Length} deferred " +
            $"notices, replay {(exchange.ReplayDue ? "yes" : "no")}");
        switch (outcome)
        {
            case AskOutcome.Resume:
                DeliverNotices(deferred);
                ResumeAfterExchange(exchange, resumeMessage ?? "ask: resuming");
                break;
            case AskOutcome.Stay:
                DeliverNotices(deferred);
                if (exchange.ReplayDue)
                {
                    _replayOnResume = true;
                    _replayOnResumeChanged |= exchange.ReplayChanged;
                }

                if (wasListening && _state == PresenterState.Paused) _guard?.Pause();
                break;
            default:
                if (deferred.Length > 0) LogMessage("info", $"ask: dropped {deferred.Length} deferred notices");
                break;
        }

        EmitAskOff(exchange, reason);
    }

    /// <summary>
    /// Resumes narration from the point the first ask interrupted: <see cref="AskExchange.NarrationHeard"/> is taken at
    /// that ask and kept across follow-ups, so answer audio never counts as narration (owner decision r1 #4).
    /// </summary>
    private void ResumeAfterExchange(AskExchange exchange, string message)
    {
        var replayDue = exchange.ReplayDue;
        var replayChanged = exchange.ReplayChanged;
        if (NarrationHeld)
        {
            ClearQuestionHold();
            SetInteraction(Interaction.None);
            EnterHold();
            return;
        }

        if (replayDue && !_wrappingUp)
        {
            LogMessage("info", message);
            ClearQuestionHold();
            SetInteraction(Interaction.None);
            ReplayCurrentSlide(replayChanged);
            return;
        }

        if (exchange.NarrationHeard && _presentation is not null)
        {
            // Ask start paused the model; only an explicit, slide-named resume makes it speak again (T8 live run).
            var current = _presentation.Slides[_slideIndex];
            ResumeAfterQuestion(message,
                PromptBuilder.ResumeAfterAskInstruction(_slideIndex, SlideCount, current.Title, exchange.FollowUps > 0));
            return;
        }

        // Asked before the slide's first narration audio: without output the resume-after-question path would stall.
        LogMessage("info", message);
        ClearQuestionHold();
        SetInteraction(Interaction.None);
        // As ResumeCore does: answer audio is not narration, so the advance (and the wrap-up end) waits for the
        // resumed audio, and the nudge or wrap-up fallback covers a silent model (T8 live-run defect class).
        _heardOutput = false;
        _nudgeCount = 0;
        ClearSilenceTimer();
        if (_wrappingUp)
        {
            _session?.AppendInstructions(PromptBuilder.WrapUpInstruction(), "wrap-up-resume");
            ArmWrapUpFallback();
            return;
        }

        if (_presentation is null) return;
        var slide = _presentation.Slides[_slideIndex];
        _session?.AppendInstructions(PromptBuilder.ResumeInstruction(_slideIndex, SlideCount, slide.Title),
            $"resume-{_slideIndex + 1}");
        ArmNudge();
    }

    // ---- Upstream reset transition (D8) ---------------------------------------------------------------------------

    /// <summary>
    /// Serialized reset: detach and suspend on the loop now, close off the loop, and re-enter with a generation-guarded
    /// <see cref="UpstreamResetClosed"/>. The next Ask or Resume reconnects a new session. Never awaits on the loop.
    /// </summary>
    private void ResetUpstream()
    {
        if (_session is not { } session) return;
        if (_state == PresenterState.Presenting) PauseCore();
        var generation = ++_resetGeneration;
        // A newer reset makes an older completion stale; the older segment is estimated here, once.
        FoldPendingReset(publish: true);
        _pendingReset = new PendingReset(session, generation, _connectedAt);
        _connectedAt = null;
        _session = null;
        _sessionInfo = null;
        _suspended = true;
        SetState(PresenterState.Paused);
        _guard?.Pause();
        LogMessage("warn", "ask: upstream reset; the next Ask or Resume reconnects");
        UpstreamStatus?.Invoke(new PresenterUpstreamStatus("suspended"));
        PublishSnapshot();
        ObserveBackground(Task.Run(() => CloseResetSessionAsync(session, generation)), "upstream reset close");
    }

    private async Task CloseResetSessionAsync(ILiveSession session, long generation)
    {
        double? seconds = null;
        var failed = false;
        try
        {
            seconds = (await session.CloseAsync().ConfigureAwait(false)).Seconds;
        }
        catch (Exception)
        {
            failed = true;
        }

        QueueFromProducer(new UpstreamResetClosed(session, generation, seconds, failed));
    }

    private async Task OnUpstreamResetClosedAsync(UpstreamResetClosed closed)
    {
        if (_pendingReset is { } pending && ReferenceEquals(pending.Session, closed.Session) &&
            pending.Generation == closed.Generation)
        {
            _pendingReset = null;
            if (closed.Failed) LogMessage("error", "ask: reset session close failed");
            AccumulateSegment(closed.Seconds, pending.ConnectedAt, publish: true);
        }

        // The reset session is disposed here, exactly once, whether the completion is current or stale.
        await DisposeSessionAsync(closed.Session, "upstream reset").ConfigureAwait(false);
    }

    /// <summary>Folds a still-pending reset segment into the usage as an estimate (usage unconfirmed).</summary>
    private void FoldPendingReset(bool publish)
    {
        if (_pendingReset is not { } pending) return;
        _pendingReset = null;
        AccumulateSegment(null, pending.ConnectedAt, publish);
    }

    // ---- Timers and frames ----------------------------------------------------------------------------------------

    private void OnAskTick(long generation)
    {
        if (generation != _askTickGeneration || _exchange is not { Phase: AskPhase.Listening } exchange) return;
        if (QuietMs(exchange) >= AskQuietTimeoutMs)
        {
            if (exchange.Recorder.HasSpeech)
            {
                AskDoneCore("quiet_sent");
            }
            else
            {
                EndExchange(AskOutcome.Stay, "quiet_cancelled");
            }

            return;
        }

        EmitListening(exchange);
    }

    private async Task OnAskEventAsync(AskEvent askEvent)
    {
        switch (askEvent)
        {
            case AskTickElapsed tick:
                OnAskTick(tick.Generation);
                break;
            case UnmuteAcked acked:
                OnUnmuteAcked(acked.Session, acked.ClientEventId);
                break;
            case UnmuteAckTimedOut timedOut:
                OnUnmuteAckTimedOut(timedOut.Generation);
                break;
            case AskBudgetElapsed budget:
                OnAskBudgetElapsed(budget.Generation);
                break;
            case UpstreamResetClosed closed:
                await OnUpstreamResetClosedAsync(closed).ConfigureAwait(false);
                break;
            case AskTranscriptChanged changed:
                OnAskTranscriptChanged(changed);
                break;
            case AskPhraseElapsed phrase:
                OnAskPhraseElapsed(phrase.Generation);
                break;
            case AskCutOffElapsed cutOff:
                OnAskCutOffElapsed(cutOff.Generation);
                break;
        }
    }

    private void ArmAskTick()
    {
        StopAskTick();
        var generation = _askTickGeneration;
        _askTickTimer = _timeProvider.CreateTimer(_ => QueueFromProducer(new AskTickElapsed(generation)), null,
            TimeSpan.FromMilliseconds(AskTickMs), TimeSpan.FromMilliseconds(AskTickMs));
    }

    private void StopAskTick()
    {
        _askTickGeneration++;
        _askTickTimer?.Dispose();
        _askTickTimer = null;
    }

    private void StopAskAck()
    {
        _askAckGeneration++;
        _askAckTimer?.Dispose();
        _askAckTimer = null;
    }

    private void StopAskBudget()
    {
        _askBudgetGeneration++;
        _askBudgetTimer?.Dispose();
        _askBudgetTimer = null;
    }

    private long QuietMs(AskExchange exchange)
    {
        var since = Math.Max(Math.Max(exchange.LastVoicedAt ?? exchange.StartedAt, exchange.StartedAt), exchange.LastExtendAt);
        return (long)_timeProvider.GetElapsedTime(since).TotalMilliseconds;
    }

    private long ElapsedMs(long since) => (long)_timeProvider.GetElapsedTime(since).TotalMilliseconds;

    private void EmitListening(AskExchange exchange)
    {
        var stats = exchange.Recorder.Stats;
        AskState?.Invoke(new PresenterAskState("listening", ElapsedMs(exchange.StartedAt),
            Math.Max(0, AskQuietTimeoutMs - QuietMs(exchange)), Math.Max(0, AskRecorder.MaxRetainedMs - stats.KeptMs),
            exchange.Recorder.HasSpeech, exchange.Transcribing, null));
    }

    private void EmitAnswering(AskExchange exchange) =>
        AskState?.Invoke(new PresenterAskState("answering", exchange.SentElapsedMs, null, null, true,
            exchange.Transcribing, exchange.SendReason));

    private void EmitAskOff(AskExchange? exchange, string reason) =>
        AskState?.Invoke(new PresenterAskState("off", exchange is null ? 0 : ElapsedMs(exchange.StartedAt), null, null,
            exchange?.Recorder.HasSpeech ?? false, exchange?.Transcribing ?? false, reason));

    private static string Seconds(long milliseconds) => (milliseconds / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    // ---- Types ----------------------------------------------------------------------------------------------------

    private enum AskPhase { Listening, Sending, AwaitingAnswer, Answering, CheckIn }

    private enum AskOutcome { Resume, Stay, Navigated, Ended }

    private enum ModelAction
    {
        ForwardAudio,
        AcceptToolCall,
        SubmitToolOutput,
        ContinueResponses,
        DelegatedResponse,
        OpenDelegationHold,
        AnnounceTool,
        ToolCommand,
        Unmute,
        ReleaseQuestionHold,
        Nudge,
        EnterHold,
        WrapUpEnd
    }

    /// <summary>A deferred model-facing instruction, kept as content so it survives an upstream reset (review r1 #2).</summary>
    private sealed record ModelNotice(string Name, string Content, string EventId);

    private sealed class AskExchange(string id, AskRecorder recorder, IAskTranscription? transcription, long startedAt,
        bool narrationHeard)
    {
        public string Id { get; private set; } = id;
        public AskRecorder Recorder { get; private set; } = recorder;
        /// <summary>Owned by the exchange until <see cref="ReleaseTranscription"/> takes and disposes it once.</summary>
        public IAskTranscription? Transcription { get; set; } = transcription;
        public bool Transcribing { get; private set; } = transcription is not null;
        public long StartedAt { get; private set; } = startedAt;
        /// <summary>The resume point: narration audio of the slide was heard before the FIRST ask (kept over follow-ups).</summary>
        public bool NarrationHeard { get; } = narrationHeard;
        public int FollowUps { get; private set; }
        public string? UnmuteEventId { get; set; }
        public long UnmuteOrdinal { get; set; }
        public AskPhase Phase { get; set; } = AskPhase.Listening;
        /// <summary>Sub-phase of Sending: the unmute is enqueued and the burst waits for its ack (P-18).</summary>
        public bool AwaitingUnmuteAck { get; set; }
        /// <summary>True while the exchange holds the upstream muted (Listening until Ask done's unmute).</summary>
        public bool UpstreamMuted { get; set; } = true;
        public long? LastVoicedAt { get; set; }
        public long LastExtendAt { get; set; } = startedAt;
        public IReadOnlyList<ReadOnlyMemory<byte>>? Chunks { get; set; }
        public string? SendReason { get; set; }
        public long SentElapsedMs { get; set; }
        public long AwaitingAnswerSince { get; set; }
        public List<ModelNotice> Deferred { get; } = [];
        public bool ReplayDue { get; set; }
        public bool ReplayChanged { get; set; }
        public long? LastUserDeltaAt { get; set; }
        public bool UtteranceOpen { get; set; }
        /// <summary>The confirmation generation an open utterance answers, fixed at its first delta; null for a check-in reply.</summary>
        public long? UtteranceConfirmation { get; set; }
        public bool FollowUpUsed { get; set; }
        /// <summary>The check-in of the current answer wait began; later model audio re-entering Answering keeps it.</summary>
        public bool CheckInBegun { get; set; }
        /// <summary>Estimated end of playback of the model audio forwarded during the answer (loop time).</summary>
        public long? PlaybackEndsAt { get; set; }
        /// <summary>The last voiced mic frame after Ask done (loop time).</summary>
        public long? LastMicVoicedAt { get; set; }
        /// <summary>Start of the check-in's reply window (loop time), for the speech-hold bound.</summary>
        public long? CheckInWindowStartsAt { get; set; }
        public bool CheckInHeld { get; set; }
        public long LastRevision { get; set; }
        public string? LatestText { get; set; }
        /// <summary>The phrase-stripped question of an ask finished by phrase; logged by length only (P-7).</summary>
        public string? Question { get; set; }
        /// <summary>The model was voiced just before this ask's start or while it listened: its early audio may be residual.</summary>
        public bool ResidualCandidate { get; set; }
        /// <summary>The first voiced frame after the send decided whether a residual is open (at most once per send).</summary>
        public bool ResidualDecided { get; set; }
        /// <summary>The rest of the pre-Ask response is arriving after the send; its audio is held until a gap.</summary>
        public bool ResidualOpen { get; set; }
        /// <summary>Arrival (loop time) of the last voiced post-send residual frame.</summary>
        public long? ResidualVoicedAt { get; set; }
        public long ResidualDroppedMs { get; set; }
        /// <summary>When the cut-off nudge of a <c>limit_sent</c> burst is due (loop time).</summary>
        public long? CutOffDueAt { get; set; }
        /// <summary>The cut-off nudge of this ask was appended; never twice.</summary>
        public bool CutOffNudged { get; set; }

        /// <summary>A follow-up ask during the answer: listen again in the same exchange (owner decision r1 #4).</summary>
        public void BeginFollowUp(string askId, AskRecorder askRecorder, IAskTranscription? askTranscription, long now)
        {
            Id = askId;
            Recorder = askRecorder;
            Transcription = askTranscription;
            Transcribing = askTranscription is not null;
            StartedAt = now;
            FollowUps++;
            Phase = AskPhase.Listening;
            AwaitingUnmuteAck = false;
            UpstreamMuted = true;
            LastVoicedAt = null;
            LastExtendAt = now;
            Chunks = null;
            SendReason = null;
            SentElapsedMs = 0;
            UnmuteEventId = null;
            UnmuteOrdinal = 0;
            UtteranceOpen = false;
            UtteranceConfirmation = null;
            FollowUpUsed = false;
            CheckInBegun = false;
            PlaybackEndsAt = null;
            LastMicVoicedAt = null;
            CheckInWindowStartsAt = null;
            CheckInHeld = false;
            LastRevision = 0;
            LatestText = null;
            Question = null;
            ResidualCandidate = false;
            ResidualDecided = false;
            ResidualOpen = false;
            ResidualVoicedAt = null;
            ResidualDroppedMs = 0;
            CutOffDueAt = null;
            CutOffNudged = false;
        }
    }

    private sealed record PendingReset(ILiveSession Session, long Generation, DateTimeOffset? ConnectedAt);

    private abstract record AskCommand : Command;
    private sealed record AskStartCommand : AskCommand;
    private sealed record AskDoneCommand : AskCommand;
    private sealed record AskExtendCommand : AskCommand;
    private sealed record AskCancelCommand : AskCommand;

    private abstract record AskEvent : PresenterEvent;
    private sealed record AskTickElapsed(long Generation) : AskEvent;
    private sealed record UnmuteAcked(ILiveSession Session, string? ClientEventId) : AskEvent;
    private sealed record UnmuteAckTimedOut(long Generation) : AskEvent;
    private sealed record AskBudgetElapsed(long Generation) : AskEvent;
    private sealed record UpstreamResetClosed(ILiveSession Session, long Generation, double? Seconds, bool Failed) : AskEvent;
    private sealed record AskTranscriptChanged(string AskId, AskTranscriptUpdate Update) : AskEvent;
    private sealed record AskPhraseElapsed(long Generation) : AskEvent;
    private sealed record AskCutOffElapsed(long Generation) : AskEvent;
}
