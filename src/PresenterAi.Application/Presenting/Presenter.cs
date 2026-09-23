using System.Text.Json;
using System.Threading.Channels;
using PresenterAi.Application.Scripts;
using PresenterAi.Application.Tools;
using PresenterToolsRegistration = PresenterAi.Application.Presenting.Tools.PresenterToolsRegistration;

namespace PresenterAi.Application.Presenting;

public sealed record SessionRequest(
    string Instructions,
    string Voice,
    string Title,
    IReadOnlyList<System.Text.Json.Nodes.JsonObject>? Tools = null,
    string? DelegationInstructions = null);

public sealed record LoadedPresentation(
    string Id,
    PresentationMeta Meta,
    IReadOnlyList<Slide> Slides,
    string? Context);

/// <summary>
/// Ports the Node presenter state machine. All mutable state and all output events are owned by its single
/// channel consumer; subscribers must not assume that notifications arrive on a caller's thread.
/// </summary>
public sealed class Presenter : IPresenter
{
    public const int NudgeMs = 15_000;
    public const int WrapUpFallbackMs = 15_000;
    public const int QuestionHoldMs = 15_000;
    public const int DefaultFollowUpWaitMs = 5_000;
    public const int MaxUpstreamAttempts = 4;
    public const int PartGapMs = 2_500;

    private readonly Func<SessionRequest, int, ILiveSession?> _createSession;
    private readonly Func<string, string, CancellationToken, Task<LoadedPresentation>> _loadPresentation;
    private readonly PresenterSettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<PresenterEvent> _events = Channel.CreateBounded<PresenterEvent>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _loop;

    private PresenterState _state = PresenterState.Idle;
    private LoadedPresentation? _presentation;
    private ILiveSession? _session;
    private LiveSessionInfo? _sessionInfo;
    private int _slideIndex;
    private bool _muted;
    private bool _heardOutput;
    private int _nudgeCount;
    private bool _wrappingUp;
    private int _outputFrames;
    private int _voicedOutputFrames;
    private int _userTranscriptCharacters;
    private bool _slideDiagnosticsActive;
    private IReadOnlyList<string> _parts = Array.Empty<string>();
    private int _partsSent;
    private double _usageSeconds;
    private double? _usageRatio;
    private LastRun? _lastRun;
    private bool _endResumable;
    private ITimer? _silenceTimer;
    private ITimer? _nudgeTimer;
    private ITimer? _wrapUpTimer;
    private ITimer? _questionTimer;
    private long _silenceGeneration;
    private long _nudgeGeneration;
    private long _wrapUpGeneration;
    private long _questionGeneration;
    private bool _questionHoldOpen;
    private bool _answerVoiced;
    private long _questionOpenedAt;
    private long? _latestQuestionEndMs;
    private readonly ToolRegistry _toolRegistry;
    private readonly Func<int, bool>? _hasDelegationModel;
    private readonly ToolRoundTracker _toolRoundTracker = new();
    private static readonly AsyncLocal<string?> ActiveToolCallId = new();
    private ToolSessionCatalogue? _catalogue;
    private long _runGeneration;
    private string? _navigatingCallId;
    private int _resumeSequence;
    private PresenterSnapshot _snapshot;
    private int _disposed;

    public Presenter(
        Func<SessionRequest, int, ILiveSession?> createSession,
        Func<string, string, CancellationToken, Task<LoadedPresentation>> loadPresentation,
        PresenterSettings? settings = null,
        TimeProvider? timeProvider = null,
        ToolRegistry? toolRegistry = null,
        Func<int, bool>? hasDelegationModel = null)
    {
        _createSession = createSession ?? throw new ArgumentNullException(nameof(createSession));
        _loadPresentation = loadPresentation ?? throw new ArgumentNullException(nameof(loadPresentation));
        _settings = settings ?? new PresenterSettings();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _toolRegistry = toolRegistry ?? new ToolRegistry();
        _hasDelegationModel = hasDelegationModel;
        PresenterToolsRegistration.RegisterAll(_toolRegistry, this);
        _snapshot = BuildSnapshot();
        _loop = Task.Run(RunLoopAsync);
    }

    public ToolRegistry ToolRegistry => _toolRegistry;
    public ToolSessionCatalogue? Catalogue => _catalogue;
    public ToolRoundTracker ToolRoundTracker => _toolRoundTracker;

    public event Action<PresenterSnapshot>? State;
    public event Action<int>? Slide;
    public event Action<PresenterAudio>? Audio;
    public event Action<PresenterTranscript>? Transcript;
    public event Action<PresenterUsage>? Usage;
    public event Action<PresenterClosed>? Closed;
    public event Action<PresenterLog>? Log;
    public event Action<PresenterUpstreamError>? UpstreamError;

    public static int PartGapFor(int advanceSilenceMs) => Math.Min(PartGapMs, (int)Math.Round(advanceSilenceMs * 0.8));

    public static bool IsNormalClose(string? reason) => reason?.Contains("request", StringComparison.Ordinal) is true;

    public PresenterSnapshot Snapshot() => Volatile.Read(ref _snapshot);

    public async Task<PresenterStartResult> StartAsync(
        string id,
        int? fromIndex,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        var command = new StartCommand(ownerId, id, fromIndex);
        ThrowIfDisposed();
        await WriteAsync(command, cancellationToken).ConfigureAwait(false);
        return await command.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> NextAsync(CancellationToken cancellationToken = default) =>
        EnqueueCommandAsync(new NextCommand(), cancellationToken);

    public Task<bool> PrevAsync(CancellationToken cancellationToken = default) =>
        EnqueueCommandAsync(new PrevCommand(), cancellationToken);

    public Task<bool> GotoAsync(int index, CancellationToken cancellationToken = default) =>
        EnqueueCommandAsync(new GotoCommand(index), cancellationToken);

    public Task<bool> PauseAsync(CancellationToken cancellationToken = default) =>
        EnqueueCommandAsync(new PauseCommand(), cancellationToken);

    public Task<bool> ResumeAsync(CancellationToken cancellationToken = default) =>
        EnqueueCommandAsync(new ResumeCommand(), cancellationToken);

    public Task<bool> MuteAsync(CancellationToken cancellationToken = default) =>
        EnqueueCommandAsync(new MuteCommand(), cancellationToken);

    public Task<bool> UnmuteAsync(CancellationToken cancellationToken = default) =>
        EnqueueCommandAsync(new UnmuteCommand(), cancellationToken);

    public Task<bool> SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken = default) =>
        EnqueueCommandAsync(new SendAudioCommand(pcm16.ToArray()), cancellationToken);

    public Task<bool> EndAsync(bool resumable = false, CancellationToken cancellationToken = default) =>
        EnqueueCommandAsync(new EndCommand(resumable), cancellationToken);

    /// <summary>Test hook that completes after all currently queued producer events have been consumed.</summary>
    public async Task WaitUntilIdleAsync(CancellationToken cancellationToken = default)
    {
        // A handler can synchronously start an async session operation which queues its callback just behind
        // this first barrier. Yield and use a second barrier so tests observe that causally-produced event too.
        await WaitForBarrierAsync(cancellationToken).ConfigureAwait(false);
        await Task.Yield();
        await WaitForBarrierAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitForBarrierAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await WriteAsync(new Barrier(completion), cancellationToken).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _events.Writer.WriteAsync(new Shutdown(completion)).ConfigureAwait(false);
            await completion.Task.ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
        }

        _events.Writer.TryComplete();
        _lifetime.Cancel();
        await _loop.ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task<bool> EnqueueCommandAsync(Command command, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        command.InvocationCallId = ActiveToolCallId.Value;
        await WriteAsync(command, cancellationToken).ConfigureAwait(false);
        return await command.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteAsync(PresenterEvent presenterEvent, CancellationToken cancellationToken = default)
    {
        try
        {
            await _events.Writer.WriteAsync(presenterEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException) when (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(Presenter));
        }
    }

    private void QueueFromProducer(PresenterEvent presenterEvent)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (!_events.Writer.TryWrite(presenterEvent))
        {
            ObserveBackground(QueueFromProducerAsync(presenterEvent), "queue producer event");
        }
    }

    private void ObserveBackground(Task task, string operation)
    {
        _ = task.ContinueWith(completed =>
        {
            if (completed.Exception is not null)
            {
                LogMessage("error", $"{operation} failed: {completed.Exception.GetBaseException().Message}");
            }
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task QueueFromProducerAsync(PresenterEvent presenterEvent)
    {
        try
        {
            await _events.Writer.WriteAsync(presenterEvent, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    private async Task RunLoopAsync()
    {
        try
        {
            await foreach (var presenterEvent in _events.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                switch (presenterEvent)
                {
                    case StartCommand start:
                        await ProcessStartAsync(start).ConfigureAwait(false);
                        break;
                    case Command command:
                        await ProcessCommandAsync(command).ConfigureAwait(false);
                        break;
                    case AudioReceived audio:
                        OnAudio(audio);
                        break;
                    case TranscriptReceived transcript:
                        OnTranscript(transcript);
                        break;
                    case UsageReceived usage:
                        OnUsage(usage);
                        break;
                    case AppendedReceived appended:
                        LogMessage("debug", $"appended {appended.Kind} {appended.ClientEventId ?? string.Empty}".Trim());
                        break;
                    case DelegationReceived delegation:
                        OnDelegation(delegation);
                        break;
                    case UpstreamErrorReceived error:
                        OnUpstreamError(error);
                        break;
                    case SessionClosed closed:
                        if (ReferenceEquals(closed.Session, _session))
                        {
                            await OnSessionClosedAsync(closed.Session, closed.Reason, closed.Seconds).ConfigureAwait(false);
                        }

                        break;
                    case SilenceElapsed silence when silence.Generation == _silenceGeneration:
                        _silenceTimer = null;
                        if (silence.PartGap)
                        {
                            OnPartGap();
                        }
                        else
                        {
                            OnSilence();
                        }

                        break;
                    case NudgeElapsed nudge when nudge.Generation == _nudgeGeneration:
                        _nudgeTimer = null;
                        OnNudge();
                        break;
                    case WrapUpFallbackElapsed fallback when fallback.Generation == _wrapUpGeneration:
                        _wrapUpTimer = null;
                        OnWrapUpFallback();
                        break;
                    case QuestionHoldElapsed question when question.Generation == _questionGeneration:
                        _questionTimer = null;
                        OnQuestionHoldElapsed();
                        break;
                    case DelegatedResponseReceived response:
                        OnDelegatedResponse(response);
                        break;
                    case ToolCallReceived toolCall:
                        OnToolCall(toolCall);
                        break;
                    case ToolInvocationCompleted completed:
                        OnToolInvocationCompleted(completed);
                        break;
                    case SessionWarning warning:
                        if (ReferenceEquals(warning.Session, _session))
                        {
                            LogMessage("warn", warning.Message);
                        }

                        break;
                    case Barrier barrier:
                        barrier.Completion.TrySetResult();
                        break;
                    case Shutdown shutdown:
                        ClearTimers();
                        _navigatingCallId = null;
                        _runGeneration++;
                        _toolRoundTracker.Clear();
                        var session = _session;
                        _session = null;
                        if (session is not null)
                        {
                            await DisposeSessionAsync(session, "shutdown").ConfigureAwait(false);
                        }

                        shutdown.Completion.TrySetResult();
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessStartAsync(StartCommand command)
    {
        try
        {
            command.Completion.TrySetResult(await StartAsyncCore(command.OwnerId, command.Id, command.FromIndex).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            command.Completion.TrySetException(exception);
        }
    }

    private async Task ProcessCommandAsync(Command command)
    {
        try
        {
            var result = command switch
            {
                NextCommand next => NextCore(next.InvocationCallId),
                PrevCommand prev => PrevCore(prev.InvocationCallId),
                GotoCommand goTo => GotoCore(goTo.Index, goTo.InvocationCallId),
                PauseCommand => PauseCore(),
                ResumeCommand => ResumeCore(),
                MuteCommand => MuteCore(),
                UnmuteCommand => UnmuteCore(),
                SendAudioCommand audio => SendAudioCore(audio.Pcm16),
                EndCommand end => await EndAsyncCore(end.Resumable).ConfigureAwait(false),
                _ => false
            };
            command.Completion.TrySetResult(result);
        }
        catch (Exception exception)
        {
            command.Completion.TrySetException(exception);
        }
    }

    private async Task<PresenterStartResult> StartAsyncCore(string ownerId, string id, int? fromIndex)
    {
        if (_state != PresenterState.Idle)
        {
            LogMessage("warn", $"start ignored: state is {StateName(_state)}");
            return new PresenterStartResult(false, id, null, null, null);
        }

        _endResumable = false;
        _navigatingCallId = null;
        _runGeneration++;
        _toolRoundTracker.Clear();
        _catalogue = _toolRegistry.CreateCatalogue(_settings.MaxInlineTools);
        SetState(PresenterState.Connecting);
        try
        {
            _presentation = await _loadPresentation(ownerId, id, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogMessage("error", $"cannot load presentation \"{id}\": {exception.Message}");
            UpstreamError?.Invoke(new PresenterUpstreamError($"cannot load presentation: {exception.Message}", "presentation"));
            SetState(PresenterState.Idle);
            return new PresenterStartResult(false, id, null, null, null);
        }

        var presentation = _presentation;
        ILiveSession? session = null;
        LiveSessionInfo? sessionInfo = null;
        string? upstream = null;
        for (var attempt = 0; attempt < MaxUpstreamAttempts; attempt++)
        {
            var isManaged = _hasDelegationModel?.Invoke(attempt) ?? true;
            var instructions = PromptBuilder.SystemInstructions(
                presentation.Meta.Title,
                presentation.Slides,
                presentation.Context,
                onWarn: message => LogMessage("warn", message),
                managedMode: isManaged);

            var inlineTools = isManaged ? _catalogue.GetInlineToolDefinitions() : null;
            var delegationInstructions = isManaged
                ? PromptBuilder.BackendInstructions(presentation.Meta.Title, presentation.Slides)
                : null;

            var request = new SessionRequest(
                instructions,
                presentation.Meta.Voice ?? _settings.Voice,
                presentation.Meta.Title,
                inlineTools,
                delegationInstructions);

            var candidate = _createSession(request, attempt);
            if (candidate is null)
            {
                break;
            }

            var label = candidate.Name ?? $"upstream #{attempt + 1}";
            try
            {
                WireSession(candidate);
                sessionInfo = await candidate.ConnectAsync(_lifetime.Token).ConfigureAwait(false);
                session = candidate;
                upstream = label;
                // Node logged this only for a fallback; the .NET host always says which upstream answered
                // (info for the primary, warn when a fallback took over) so a live run shows Azure vs OpenAI.
                LogMessage(attempt > 0 ? "warn" : "info", $"connected via {label}");
                break;
            }
            catch (Exception exception)
            {
                LogMessage("error", $"session start via {label} failed: {exception.Message}");
                var startup = exception as LiveStartupException;
                UpstreamError?.Invoke(new PresenterUpstreamError($"{label}: {exception.Message}", startup?.Code ?? "connect"));
                await DisposeSessionAsync(candidate, $"failed start via {label}").ConfigureAwait(false);
            }
        }

        if (session is null || sessionInfo is null)
        {
            LogMessage("error", "no upstream could start a session");
            SetState(PresenterState.Idle);
            return new PresenterStartResult(false, id, null, null, null);
        }

        _session = session;
        _sessionInfo = sessionInfo;
        var startAt = fromIndex
            ?? (_lastRun is { EndedNormally: false } last && last.Id == id ? last.Index : 0);
        startAt = Math.Clamp(startAt, 0, Math.Max(0, presentation.Slides.Count - 1));
        _muted = false;
        _usageSeconds = 0;
        _usageRatio = null;
        SetState(PresenterState.Presenting);
        PresentSlide(startAt, interrupt: false);
        return new PresenterStartResult(true, id, upstream, sessionInfo.Id, sessionInfo.Model);
    }

    private void WireSession(ILiveSession session)
    {
        session.Audio += (bytes, startMs, endMs) => QueueFromProducer(new AudioReceived(session, bytes.ToArray(), startMs, endMs));
        session.Transcript += (role, delta, startMs, endMs) => QueueFromProducer(new TranscriptReceived(session, role, delta, startMs, endMs));
        session.Appended += (kind, clientEventId, raw) => QueueFromProducer(new AppendedReceived(session, kind, clientEventId, raw.Clone()));
        session.Usage += (seconds, ratio) => QueueFromProducer(new UsageReceived(session, seconds, ratio));
        session.Delegation += raw => QueueFromProducer(new DelegationReceived(session, raw.Clone()));
        session.UpstreamError += raw => QueueFromProducer(new UpstreamErrorReceived(session, raw.Clone()));
        session.Warning += message => QueueFromProducer(new SessionWarning(session, message));
        session.DelegatedResponseFinished += (id, type) => QueueFromProducer(new DelegatedResponseReceived(session, id, type));
        session.ToolCallRequested += (delegationId, callId, name, arguments) => QueueFromProducer(new ToolCallReceived(session, delegationId, callId, name, arguments));
        session.Closed += (reason, seconds) => QueueFromProducer(new SessionClosed(session, reason, seconds));
    }

    private void PresentSlide(int index, bool interrupt)
    {
        if (_presentation is null || index < 0 || index >= _presentation.Slides.Count)
        {
            return;
        }

        CompleteSlideDiagnostics();
        ClearTimers();
        _slideIndex = index;
        _heardOutput = false;
        _nudgeCount = 0;
        _wrappingUp = false;
        StartSlideDiagnostics();
        var slide = _presentation.Slides[index];
        LogMessage("info", $"slide {index + 1}/{SlideCount}{(slide.Title.Length > 0 ? $" — {slide.Title}" : string.Empty)}");
        Slide?.Invoke(index);
        PublishSnapshot();

        if (!string.IsNullOrEmpty(slide.Notes))
        {
            _session?.AppendThinking(
                PromptBuilder.NotesContext(index, SlideCount, slide.Title, slide.Notes),
                $"slide-{index + 1}-notes");
        }

        _parts = TextChunker.Chunk(slide.Narration, _presentation.Meta.ChunkChars);
        _partsSent = 0;
        if (_parts.Count == 0)
        {
            LogMessage("info", $"slide {index + 1} has no narration; advancing after {AdvanceSilenceMs} ms");
            _heardOutput = true;
            ArmSilence();
            return;
        }

        if (_parts.Count > 1)
        {
            LogMessage("info", $"slide {index + 1} narration is sent in {_parts.Count} parts");
        }

        SendNextPart(interrupt);
        ArmNudge();
    }

    private bool SendNextPart(bool interrupt = false)
    {
        if (_presentation is null || _partsSent >= _parts.Count)
        {
            return false;
        }

        var slide = _presentation.Slides[_slideIndex];
        var part = _partsSent;
        _session?.AppendInstructions(
            PromptBuilder.SlideInstruction(_slideIndex, SlideCount, slide.Title, _parts[part], part + 1, _parts.Count, interrupt && part == 0),
            $"slide-{_slideIndex + 1}-part-{part + 1}");
        _partsSent = part + 1;
        if (part > 0)
        {
            LogMessage("info", $"slide {_slideIndex + 1}: sent part {part + 1}/{_parts.Count}");
        }

        return true;
    }

    private void OnAudio(AudioReceived audio)
    {
        if (!ReferenceEquals(audio.Session, _session))
        {
            return;
        }

        Audio?.Invoke(new PresenterAudio(audio.Bytes, audio.StartMs, audio.EndMs));
        var voiced = AudioLevel.IsVoiced(audio.Bytes);
        if (_slideDiagnosticsActive)
        {
            _outputFrames++;
            if (voiced)
            {
                _voicedOutputFrames++;
            }
        }

        if (_state is not (PresenterState.Presenting or PresenterState.Paused) || !voiced)
        {
            return;
        }

        if (!_heardOutput)
        {
            _heardOutput = true;
            ClearNudgeTimer();
        }

        if (_state == PresenterState.Presenting)
        {
            // While a backend answer is pending, speech is filler ("One moment."), not the answer.
            if (_questionHoldOpen && !_toolRoundTracker.HasPendingBackendDelegation && IsAfterLatestQuestion(audio))
            {
                if (!_answerVoiced)
                {
                    _answerVoiced = true;
                    // The follow-up window takes over from here; the 15 s timer must not cut a long answer short.
                    ClearQuestionTimer();
                    var elapsed = (long)_timeProvider.GetElapsedTime(_questionOpenedAt).TotalMilliseconds;
                    LogMessage("info", $"question: answered after {elapsed} ms");
                }
            }

            if (_questionHoldOpen && _answerVoiced)
            {
                SetSilenceTimer(FollowUpWaitMs, partGap: false);
            }
            else
            {
                ArmAfterVoice();
            }
        }
    }

    private void OnTranscript(TranscriptReceived transcript)
    {
        if (!ReferenceEquals(transcript.Session, _session))
        {
            return;
        }

        Transcript?.Invoke(new PresenterTranscript(transcript.Role, transcript.Delta, transcript.StartMs, transcript.EndMs));
        if (transcript.Role == "user" && _slideDiagnosticsActive)
        {
            _userTranscriptCharacters += transcript.Delta.Length;
        }

        if (transcript.Role == "user" && _state == PresenterState.Presenting && !string.IsNullOrWhiteSpace(transcript.Delta))
        {
            OpenOrExtendQuestionHold(transcript.EndMs);
        }
    }

    private void OnUsage(UsageReceived usage)
    {
        if (!ReferenceEquals(usage.Session, _session))
        {
            return;
        }

        _usageSeconds = usage.Seconds;
        _usageRatio = usage.Ratio;
        Usage?.Invoke(new PresenterUsage(usage.Seconds, usage.Ratio));
    }

    private void OnDelegation(DelegationReceived delegation)
    {
        if (!ReferenceEquals(delegation.Session, _session))
        {
            return;
        }

        var payload = delegation.Raw.ValueKind == JsonValueKind.Object
            && delegation.Raw.TryGetProperty("delegation", out var nested)
            ? nested
            : default;
        var target = JsonString(payload, "target");
        var id = JsonString(payload, "id");
        if (target is "client" or "responses")
        {
            LogMessage("info", $"question: delegated ({(target == "client" ? "client" : "backend")})");
        }

        // A delegation that arrives while paused, ending or closed must not make the model talk through that state.
        // A late one after navigation still holds the new slide: GPT-Live speaks its answer regardless (there is no
        // cancel), and the hold keeps the next part from talking over it.
        if (_state != PresenterState.Presenting)
        {
            return;
        }

        OpenOrExtendQuestionHold(null);
        if (target == "responses")
        {
            _toolRoundTracker.OpenDelegation(id ?? string.Empty);
        }
        else if (target == "client")
        {
            _session?.AppendInstructions(
                PromptBuilder.ClientDelegationAnswerNowInstruction(),
                $"question-{id ?? "client"}-answer-now",
                id);
        }
    }

    private void OnDelegatedResponse(DelegatedResponseReceived response)
    {
        if (!ReferenceEquals(response.Session, _session))
        {
            return;
        }

        var result = _toolRoundTracker.OnDelegatedResponseFinished(response.DelegationId, response.Type);
        switch (result)
        {
            case ToolRoundTracker.ResponseFinishedResult.ToolRound:
                LogMessage("info", $"question: backend tool round completed for {response.DelegationId}");
                if (_toolRoundTracker.ShouldSendContinueResponses())
                {
                    _session?.ContinueResponses();
                    _toolRoundTracker.OnResponsesContinued();
                }

                break;

            case ToolRoundTracker.ResponseFinishedResult.FinalAnswer:
                FinishBackendDelegation("response.completed");
                break;

            case ToolRoundTracker.ResponseFinishedResult.Failed:
                FinishBackendDelegation(response.Type);
                break;

            case ToolRoundTracker.ResponseFinishedResult.Ignored:
                break;
        }
    }

    private void FinishBackendDelegation(string type)
    {
        if (type == "response.completed")
        {
            LogMessage("info", "question: backend answer ready");
        }
        else
        {
            LogMessage("warn", $"question: backend answer failed ({type})");
        }

        // Give the live model a fresh window to speak the injected answer.
        ArmQuestionHold();
    }

    private void OnUpstreamError(UpstreamErrorReceived error)
    {
        if (!ReferenceEquals(error.Session, _session))
        {
            return;
        }

        var clientEventId = JsonString(error.Raw, "client_event_id");
        var code = JsonString(error.Raw, "code");
        var message = JsonString(error.Raw, "message") ?? "unknown";
        LogMessage("error", $"upstream error{(clientEventId is null ? string.Empty : $" for {clientEventId}")}: {code ?? string.Empty} {message}".Trim());
        UpstreamError?.Invoke(new PresenterUpstreamError(message, code, clientEventId));

        // A delegated backend can also fail with a top-level error that names no delegation; it ends the pending one.
        if (code == "backend_error" && _toolRoundTracker.HasPendingBackendDelegation)
        {
            _toolRoundTracker.CloseAllDelegations();
            FinishBackendDelegation(code);
        }
    }

    private void OnToolCall(ToolCallReceived call)
    {
        if (!ReferenceEquals(call.Session, _session))
        {
            LogMessage("info", $"tool call for {call.CallId} dropped: session replaced");
            return;
        }

        if (_toolRoundTracker.IsCallDuplicate(call.CallId))
        {
            LogMessage("info", $"tool call for {call.CallId} ignored: duplicate call id");
            return;
        }

        if (!_toolRoundTracker.AddCall(call.DelegationId, call.CallId, call.Session, _runGeneration))
        {
            LogMessage("info", $"tool call for {call.CallId} ignored: could not track");
            return;
        }

        LogMessage("info", $"tool: {call.Name} ({call.CallId}) requested for delegation {call.DelegationId}");
        StartToolInvocation(call.Session, _runGeneration, call.DelegationId, call.CallId, call.Name, call.Arguments);
    }

    private void StartToolInvocation(
        ILiveSession session,
        long runGeneration,
        string delegationId,
        string callId,
        string name,
        string argumentsJson)
    {
        var catalogue = _catalogue;
        _ = Task.Run(async () =>
        {
            ActiveToolCallId.Value = callId;
            ToolResult result = ToolResult.Failure("tool failed");
            try
            {
                if (catalogue is null)
                {
                    result = ToolResult.Failure("no tool catalogue available");
                }
                else
                {
                    JsonDocument? doc = null;
                    try
                    {
                        doc = JsonDocument.Parse(argumentsJson);
                    }
                    catch (JsonException)
                    {
                        result = ToolResult.Failure("malformed argument JSON");
                    }

                    if (doc is not null)
                    {
                        using (doc)
                        {
                            var resolution = catalogue.Resolve(name, doc.RootElement);
                            if (!resolution.IsResolved)
                            {
                                result = resolution.Error!;
                            }
                            else
                            {
                                var tool = resolution.Tool!;
                                using var cts = new CancellationTokenSource(tool.Timeout);
                                try
                                {
                                    result = await tool.InvokeAsync(resolution.Arguments, cts.Token).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                                {
                                    result = ToolResult.Failure("timed out") with { Outcome = "timeout" };
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                result = ToolResult.Failure("tool failed");
            }

            QueueFromProducer(new ToolInvocationCompleted(session, runGeneration, delegationId, callId, result));
        });
    }

    private void OnToolInvocationCompleted(ToolInvocationCompleted completed)
    {
        if (!ReferenceEquals(completed.Session, _session))
        {
            LogMessage("info", $"tool invocation for {completed.CallId} dropped: session replaced");
            return;
        }

        if (completed.RunGeneration != _runGeneration && completed.CallId != _navigatingCallId)
        {
            LogMessage("info", $"tool invocation for {completed.CallId} dropped: stale generation ({completed.RunGeneration} vs {_runGeneration})");
            return;
        }

        if (!_toolRoundTracker.IsCallPending(completed.DelegationId, completed.CallId))
        {
            LogMessage("info", $"tool invocation for {completed.CallId} dropped: call not pending in tracker");
            return;
        }

        _toolRoundTracker.MarkCallSubmitted(completed.DelegationId, completed.CallId);
        if (_navigatingCallId == completed.CallId)
        {
            _navigatingCallId = null;
        }

        _session?.SubmitToolOutput(completed.CallId, completed.Result.ToJsonString());
        LogMessage("info", $"tool: {completed.CallId} output submitted (ok={completed.Result.Ok})");

        if (_toolRoundTracker.ShouldSendContinueResponses())
        {
            _session?.ContinueResponses();
            _toolRoundTracker.OnResponsesContinued();
        }
    }

    private void OnPartGap()
    {
        if (HoldBlocksProgress())
        {
            return;
        }

        if (_state == PresenterState.Presenting && PartsPending)
        {
            SendNextPart();
        }
    }

    private void OnSilence()
    {
        if (HoldBlocksProgress())
        {
            return;
        }

        if (_state != PresenterState.Presenting || !_heardOutput || PartsPending)
        {
            return;
        }

        if (_wrappingUp)
        {
            LogMessage("info", "wrap-up finished; ending session");
            ObserveBackground(EndAsync(), "wrap-up end");
            return;
        }

        if (_slideIndex < SlideCount - 1)
        {
            LogMessage("info", $"advance → slide {_slideIndex + 2}");
            PresentSlide(_slideIndex + 1, interrupt: false);
        }
        else
        {
            StartWrapUp();
        }
    }

    private void OnNudge()
    {
        if (_state != PresenterState.Presenting || _heardOutput || _presentation is null)
        {
            return;
        }

        var slide = _presentation.Slides[_slideIndex];
        switch (_nudgeCount)
        {
            case 0:
                _nudgeCount = 1;
                LogMessage("warn", $"no output audio {NudgeMs} ms after slide {_slideIndex + 1} was sent; nudging the model");
                _session?.AppendInstructions(
                    PromptBuilder.NudgeInstruction(_slideIndex, SlideCount, slide.Title),
                    $"slide-{_slideIndex + 1}-nudge");
                ArmNudge();
                break;
            case 1:
                _nudgeCount = 2;
                LogMessage("warn", $"no output audio {NudgeMs * 2} ms after slide {_slideIndex + 1} was sent; nudging the model again");
                _session?.AppendInstructions(
                    PromptBuilder.NudgeInstruction(_slideIndex, SlideCount, slide.Title),
                    $"slide-{_slideIndex + 1}-nudge-2");
                ArmNudge();
                break;
            default:
                CompleteSlideDiagnostics();
                PauseCore();
                LogMessage("warn", $"The model stopped responding on slide {_slideIndex + 1} — Resume or End.");
                break;
        }
    }

    private void OnWrapUpFallback()
    {
        if (_questionHoldOpen)
        {
            return;
        }

        if (_state == PresenterState.Presenting && _wrappingUp && !_heardOutput)
        {
            LogMessage("warn", "no wrap-up audio; ending session");
            ObserveBackground(EndAsync(), "wrap-up fallback end");
        }
    }

    private void StartWrapUp()
    {
        if (_wrappingUp)
        {
            return;
        }

        CompleteSlideDiagnostics();
        ClearTimers();
        _wrappingUp = true;
        _parts = Array.Empty<string>();
        _partsSent = 0;
        _heardOutput = false;
        LogMessage("info", "last slide finished; sending wrap-up");
        _session?.AppendInstructions(PromptBuilder.WrapUpInstruction(), "wrap-up");
        ArmWrapUpFallback();
    }

    private bool NextCore(string? sourceCallId = null)
    {
        if (!CanNavigate())
        {
            return false;
        }

        LeavePauseForNavigation();
        if (_slideIndex >= SlideCount - 1)
        {
            OnNavigationSucceeded(sourceCallId);
            StartWrapUp();
            return true;
        }

        LogMessage("info", $"manual next → slide {_slideIndex + 2}");
        OnNavigationSucceeded(sourceCallId);
        PresentSlide(_slideIndex + 1, interrupt: true);
        return true;
    }

    private bool PrevCore(string? sourceCallId = null)
    {
        if (!CanNavigate())
        {
            return false;
        }

        LeavePauseForNavigation();
        var target = Math.Max(0, _slideIndex - 1);
        LogMessage("info", $"manual prev → slide {target + 1}");
        OnNavigationSucceeded(sourceCallId);
        PresentSlide(target, interrupt: true);
        return true;
    }

    private bool GotoCore(int index, string? sourceCallId = null)
    {
        if (!CanNavigate())
        {
            return false;
        }

        if (index < 0 || index >= SlideCount)
        {
            LogMessage("warn", $"goto ignored: index {index} out of range");
            return false;
        }

        LeavePauseForNavigation();
        LogMessage("info", $"goto → slide {index + 1}");
        OnNavigationSucceeded(sourceCallId);
        PresentSlide(index, interrupt: true);
        return true;
    }

    private void OnNavigationSucceeded(string? sourceCallId)
    {
        _navigatingCallId = sourceCallId;
        _runGeneration++;
    }

    private bool PauseCore()
    {
        if (_state != PresenterState.Presenting)
        {
            return false;
        }

        ClearTimers();
        _session?.Mute();
        _session?.AppendInstructions(PromptBuilder.PauseInstruction(), $"pause-{_slideIndex + 1}");
        SetState(PresenterState.Paused);
        return true;
    }

    private bool ResumeCore()
    {
        if (_state != PresenterState.Paused || _presentation is null)
        {
            return false;
        }

        if (!_muted)
        {
            _session?.Unmute();
        }

        var slide = _presentation.Slides[_slideIndex];
        _heardOutput = false;
        _nudgeCount = 0;
        if (!_slideDiagnosticsActive)
        {
            // A stall pause closed this slide's counts; the resumed part of the slide gets its own line.
            StartSlideDiagnostics();
        }

        SetState(PresenterState.Presenting);
        if (_wrappingUp)
        {
            _session?.AppendInstructions(PromptBuilder.WrapUpInstruction(), "wrap-up-resume");
        }
        else
        {
            _session?.AppendInstructions(
                PromptBuilder.ResumeInstruction(_slideIndex, SlideCount, slide.Title),
                $"resume-{_slideIndex + 1}");
        }

        ArmNudge();
        return true;
    }

    private bool MuteCore()
    {
        _muted = true;
        if (_state == PresenterState.Presenting)
        {
            _session?.Mute();
        }

        PublishSnapshot();
        return true;
    }

    private bool UnmuteCore()
    {
        _muted = false;
        if (_state == PresenterState.Presenting)
        {
            _session?.Unmute();
        }

        PublishSnapshot();
        return true;
    }

    private bool SendAudioCore(byte[] pcm16) =>
        _state == PresenterState.Presenting && !_muted && (_session?.SendAudio(pcm16) ?? false);

    private async Task<bool> EndAsyncCore(bool resumable)
    {
        if (_state is PresenterState.Idle or PresenterState.Ending)
        {
            return false;
        }

        CompleteSlideDiagnostics();
        ClearTimers();
        _navigatingCallId = null;
        _runGeneration++;
        _toolRoundTracker.Clear();
        _endResumable = resumable;
        var session = _session;
        SetState(PresenterState.Ending);
        if (session is null)
        {
            OnClosed("close_requested", null);
            return true;
        }

        try
        {
            await session.CloseAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            LogMessage("error", $"session close failed: {exception.Message}");
            OnClosed("connection_lost", null);
            await DisposeSessionAsync(session, "failed close").ConfigureAwait(false);
            throw;
        }
    }

    private async Task OnSessionClosedAsync(ILiveSession session, string reason, double? seconds)
    {
        OnClosed(reason, seconds);
        await DisposeSessionAsync(session, "session close").ConfigureAwait(false);
    }

    private async Task DisposeSessionAsync(ILiveSession session, string operation)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogMessage("error", $"session dispose after {operation} failed: {exception.Message}");
        }
    }

    private void OnClosed(string reason, double? seconds)
    {
        CompleteSlideDiagnostics();
        ClearTimers();
        _navigatingCallId = null;
        _runGeneration++;
        _toolRoundTracker.Clear();
        var endedNormally = IsNormalClose(reason) && !_endResumable;
        if (_presentation is not null)
        {
            _lastRun = new LastRun(_presentation.Id, _slideIndex, endedNormally);
        }

        if (seconds.HasValue)
        {
            _usageSeconds = seconds.Value;
        }

        _session = null;
        _sessionInfo = null;
        LogMessage(
            "info",
            $"closed: reason={reason} usage={(seconds.HasValue ? seconds.Value.ToString() : "unconfirmed")} s{(endedNormally ? string.Empty : $" (Start resumes at slide {_slideIndex + 1})")}");
        Closed?.Invoke(new PresenterClosed(reason, seconds));
        SetState(PresenterState.Idle);
    }

    private void StartSlideDiagnostics()
    {
        _outputFrames = 0;
        _voicedOutputFrames = 0;
        _userTranscriptCharacters = 0;
        _slideDiagnosticsActive = true;
    }

    private void CompleteSlideDiagnostics()
    {
        if (!_slideDiagnosticsActive)
        {
            return;
        }

        LogMessage(
            "info",
            $"slide {_slideIndex + 1}: output {_outputFrames} frames ({_voicedOutputFrames} voiced), user transcript {_userTranscriptCharacters} chars");
        _slideDiagnosticsActive = false;
    }

    private void OnQuestionHoldElapsed()
    {
        if (!_questionHoldOpen)
        {
            return;
        }

        ResumeAfterQuestion("question: released after 15 s without an answer");
    }

    // An open hold never lets a timer advance. Once the answer has been followed by FollowUpWaitMs of quiet, the
    // model is told to resume the slide; the ordinary timers take over from there.
    private bool HoldBlocksProgress()
    {
        if (!_questionHoldOpen)
        {
            return false;
        }

        if (_answerVoiced)
        {
            ResumeAfterQuestion($"question: no follow-up after {FollowUpWaitMs} ms; resuming");
        }

        return true;
    }

    private void ResumeAfterQuestion(string message)
    {
        LogMessage("info", message);
        ClearQuestionHold();
        if (_heardOutput)
        {
            _session?.AppendInstructions(
                PromptBuilder.ResumeAfterQuestionInstruction(),
                $"slide-{_slideIndex + 1}-resume-{++_resumeSequence}");
            ArmAfterVoice();
        }
        else if (_wrappingUp)
        {
            ArmWrapUpFallback();
        }
    }

    private void ArmAfterVoice()
    {
        if (PartsPending)
        {
            SetSilenceTimer(PartGapFor(AdvanceSilenceMs), partGap: true);
        }
        else
        {
            ArmSilence();
        }
    }

    private void ArmSilence() => SetSilenceTimer(AdvanceSilenceMs, partGap: false);

    private void ArmNudge()
    {
        ClearNudgeTimer();
        var generation = ++_nudgeGeneration;
        _nudgeTimer = _timeProvider.CreateTimer(
            _ => QueueFromProducer(new NudgeElapsed(generation)),
            null,
            TimeSpan.FromMilliseconds(NudgeMs),
            Timeout.InfiniteTimeSpan);
    }

    private void ArmWrapUpFallback()
    {
        ClearWrapUpTimer();
        var generation = ++_wrapUpGeneration;
        _wrapUpTimer = _timeProvider.CreateTimer(
            _ => QueueFromProducer(new WrapUpFallbackElapsed(generation)),
            null,
            TimeSpan.FromMilliseconds(WrapUpFallbackMs),
            Timeout.InfiniteTimeSpan);
    }

    private void ArmQuestionHold()
    {
        ClearQuestionTimer();
        var generation = ++_questionGeneration;
        _questionTimer = _timeProvider.CreateTimer(
            _ => QueueFromProducer(new QuestionHoldElapsed(generation)),
            null,
            TimeSpan.FromMilliseconds(QuestionHoldMs),
            Timeout.InfiniteTimeSpan);
    }

    private void SetSilenceTimer(int milliseconds, bool partGap)
    {
        ClearSilenceTimer();
        var generation = ++_silenceGeneration;
        _silenceTimer = _timeProvider.CreateTimer(
            _ => QueueFromProducer(new SilenceElapsed(generation, partGap)),
            null,
            TimeSpan.FromMilliseconds(milliseconds),
            Timeout.InfiniteTimeSpan);
    }

    private void ClearTimers()
    {
        ClearSilenceTimer();
        ClearNudgeTimer();
        ClearWrapUpTimer();
        ClearQuestionHold();
    }

    private void ClearSilenceTimer()
    {
        _silenceGeneration++;
        _silenceTimer?.Dispose();
        _silenceTimer = null;
    }

    private void ClearNudgeTimer()
    {
        _nudgeGeneration++;
        _nudgeTimer?.Dispose();
        _nudgeTimer = null;
    }

    private void ClearWrapUpTimer()
    {
        _wrapUpGeneration++;
        _wrapUpTimer?.Dispose();
        _wrapUpTimer = null;
    }

    private void OpenOrExtendQuestionHold(long? endMs)
    {
        if (!_questionHoldOpen)
        {
            _questionHoldOpen = true;
            LogMessage("info", "question: hold opened");
        }

        _answerVoiced = false;
        _latestQuestionEndMs = endMs;
        _questionOpenedAt = _timeProvider.GetTimestamp();
        ClearSilenceTimer();
        ClearWrapUpTimer();
        ArmQuestionHold();
    }

    private void ClearQuestionHold()
    {
        _questionHoldOpen = false;
        _answerVoiced = false;
        _latestQuestionEndMs = null;
        ClearQuestionTimer();
    }

    private void ClearQuestionTimer()
    {
        _questionGeneration++;
        _questionTimer?.Dispose();
        _questionTimer = null;
    }

    private bool IsAfterLatestQuestion(AudioReceived audio) =>
        _latestQuestionEndMs is null || audio.EndMs is null || audio.EndMs >= _latestQuestionEndMs;

    private bool CanNavigate() => _state is PresenterState.Presenting or PresenterState.Paused;

    private void LeavePauseForNavigation()
    {
        if (_state != PresenterState.Paused)
        {
            return;
        }

        if (!_muted)
        {
            _session?.Unmute();
        }

        SetState(PresenterState.Presenting);
    }

    private bool PartsPending => _partsSent < _parts.Count;

    private int SlideCount => _presentation?.Slides.Count ?? 0;

    private int AdvanceSilenceMs => _presentation?.Meta.AdvanceSilenceMs ?? _settings.AdvanceSilenceMs;

    private int FollowUpWaitMs => _settings.FollowUpWaitMs;

    public PresenterSettings Settings => _settings;

    private void SetState(PresenterState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        LogMessage("info", $"state → {StateName(state)}");
        PublishSnapshot();
    }

    private void PublishSnapshot()
    {
        var snapshot = BuildSnapshot();
        Volatile.Write(ref _snapshot, snapshot);
        State?.Invoke(snapshot);
    }

    private PresenterSnapshot BuildSnapshot() => new(
        StateName(_state),
        _presentation?.Id,
        _presentation?.Meta.Title,
        _slideIndex,
        SlideCount,
        _state == PresenterState.Paused,
        _muted,
        _session?.Id,
        _sessionInfo?.ExpiresAt,
        _usageSeconds,
        AdvanceSilenceMs);

    private void LogMessage(string level, string message) => Log?.Invoke(new PresenterLog(level, message));

    private static string StateName(PresenterState state) => state.ToString().ToLowerInvariant();

    private static string? JsonString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(Presenter));
        }
    }

    private abstract record PresenterEvent;
    private abstract record Command : PresenterEvent
    {
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? InvocationCallId { get; set; }
    }

    private sealed record StartCommand(string OwnerId, string Id, int? FromIndex) : PresenterEvent
    {
        public TaskCompletionSource<PresenterStartResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed record NextCommand : Command;
    private sealed record PrevCommand : Command;
    private sealed record GotoCommand(int Index) : Command;
    private sealed record PauseCommand : Command;
    private sealed record ResumeCommand : Command;
    private sealed record MuteCommand : Command;
    private sealed record UnmuteCommand : Command;
    private sealed record SendAudioCommand(byte[] Pcm16) : Command;
    private sealed record EndCommand(bool Resumable) : Command;
    private sealed record AudioReceived(ILiveSession Session, byte[] Bytes, long? StartMs, long? EndMs) : PresenterEvent;
    private sealed record TranscriptReceived(ILiveSession Session, string Role, string Delta, long? StartMs, long? EndMs) : PresenterEvent;
    private sealed record UsageReceived(ILiveSession Session, double Seconds, double? Ratio) : PresenterEvent;
    private sealed record AppendedReceived(ILiveSession Session, string Kind, string? ClientEventId, JsonElement Raw) : PresenterEvent;
    private sealed record DelegationReceived(ILiveSession Session, JsonElement Raw) : PresenterEvent;
    private sealed record UpstreamErrorReceived(ILiveSession Session, JsonElement Raw) : PresenterEvent;
    private sealed record DelegatedResponseReceived(ILiveSession Session, string DelegationId, string Type) : PresenterEvent;
    private sealed record ToolCallReceived(ILiveSession Session, string DelegationId, string CallId, string Name, string Arguments) : PresenterEvent;
    private sealed record ToolInvocationCompleted(ILiveSession Session, long RunGeneration, string DelegationId, string CallId, ToolResult Result) : PresenterEvent;
    private sealed record SessionWarning(ILiveSession Session, string Message) : PresenterEvent;
    private sealed record SessionClosed(ILiveSession Session, string Reason, double? Seconds) : PresenterEvent;
    private sealed record SilenceElapsed(long Generation, bool PartGap) : PresenterEvent;
    private sealed record NudgeElapsed(long Generation) : PresenterEvent;
    private sealed record WrapUpFallbackElapsed(long Generation) : PresenterEvent;
    private sealed record QuestionHoldElapsed(long Generation) : PresenterEvent;
    private sealed record Barrier(TaskCompletionSource Completion) : PresenterEvent;
    private sealed record Shutdown(TaskCompletionSource Completion) : PresenterEvent;
    private sealed record LastRun(string Id, int Index, bool EndedNormally);
}
