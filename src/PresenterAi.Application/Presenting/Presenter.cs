using System.Text.Json;
using System.Threading.Channels;
using PresenterAi.Application.Scripts;
using PresenterAi.Application.Scripts.Revisions;
using PresenterAi.Application.Tools;
using PresenterAi.Application.Tools.External;
using System.Text.Json.Nodes;
using System.Diagnostics;
using PresenterAi.Application.Presenting.VoiceCommands;
using PresenterToolsRegistration = PresenterAi.Application.Presenting.Tools.PresenterToolsRegistration;

namespace PresenterAi.Application.Presenting;

public sealed record SessionRequest(
    string Instructions,
    string Voice,
    string Title,
    IReadOnlyList<System.Text.Json.Nodes.JsonObject>? Tools = null,
    string? DelegationInstructions = null,
    IReadOnlyList<JsonObject>? HostedTools = null);

public sealed record LoadedPresentation(
    string Id,
    PresentationMeta Meta,
    IReadOnlyList<Slide> Slides,
    string? Context,
    int? Version = null);

/// <summary>
/// Ports the Node presenter state machine. All mutable state and all output events are owned by its single
/// channel consumer; subscribers must not assume that notifications arrive on a caller's thread.
/// </summary>
public sealed partial class Presenter : IPresenter
{
    public const int NudgeMs = 15_000;
    public const int WrapUpFallbackMs = 15_000;
    public const int QuestionHoldMs = 15_000;
    public const int DefaultFollowUpWaitMs = 5_000;
    public const int MaxUpstreamAttempts = 4;
    public const int PartGapMs = 2_500;
    // DisposeAsync's wait for the loop. The shutdown close is LiveSession.CloseAsync, itself bounded by
    // LiveSessionOptions.CloseTimeout (5 s), so twice that expires only for a loop wedged elsewhere, e.g. in a
    // presentation loader that ignores cancellation. Past it the process can exit; no upstream can be created.
    public static readonly TimeSpan ShutdownBound = TimeSpan.FromSeconds(10);

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
    private readonly Func<string, CancellationToken, Task<SessionToolSet>>? _loadSessionTools;
    private readonly TimeSpan _startToolBudget;
    private SessionToolSet? _sessionTools;
    private PendingToolConfirmation? _pendingTool;
    private readonly Dictionary<string, ApprovedToolCall> _approvedTools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _hostedStarted = new(StringComparer.Ordinal);
    private readonly ToolRoundTracker _toolRoundTracker = new();
    private static readonly AsyncLocal<string?> ActiveToolCallId = new();
    private ToolSessionCatalogue? _catalogue;
    private long _runGeneration;
    private readonly Dictionary<string, long> _navigatingCallIds = new(StringComparer.Ordinal);
    private int _resumeSequence;
    private enum Interaction { None, Answering, AwaitingCarryOn, WaitingOnSlide, AwaitingEndQuestion, AwaitingEndAnswer, AwaitingConfirmQuestion, AwaitingConfirmAnswer }
    private Interaction _interaction;
    private bool _rangeReply;
    private ITimer? _interactionTimer;
    private long _interactionGeneration;
    private ITimer? _utteranceTimer;
    private long _utteranceGeneration;
    private string _utterance = "";
    private long? _utteranceStartMs;
    private long? _utteranceEndMs;
    private long _utteranceOpenedAt;
    private bool _utteranceTooLong;
    private bool _utteranceDuringSpeech;
    private bool _utteranceBeganDuringCarryOn;
    private bool _utteranceBeganDuringWaiting;
    private readonly Queue<(long Start, long End)> _voicedIntervals = new();
    private long _lastVoicedAt;
    private long? _permitBarrierMs;
    private bool _speechPermit;
    private bool _endQuestionVoiced;
    private long _lastAnswerAt;
    private ITimer? _permitTimer;
    private long _permitGeneration;
    private PresenterSnapshot _snapshot;
    private int _disposed;
    private TalkGuard? _guard;
    private string? _requestedEndReason;
    private DateTimeOffset? _talkStartedAt;
    private DateTimeOffset? _talkEndedAt;
    private double _priorUsageSeconds;
    private double _priorEstimatedSeconds;
    private bool _usageConfirmed = true;
    private DateTimeOffset? _connectedAt;
    private CancellationTokenSource? _runCts;
    // Off-loop signals (End, the guard's max-length callback, AbortPendingStart, Dispose) reach a Start or talk only
    // through tickets: a ticket exists from the moment StartAsync enqueues, so there is no queued or accepting
    // window in which a signal finds nothing to cancel. _queuedStarts and _talk are changed under _ticketGate.
    private readonly object _ticketGate = new();
    private readonly List<StartTicket> _queuedStarts = [];
    private StartTicket? _talk;
    private bool _suspended;
    private string? _endDiagnostic;

    public Presenter(
        Func<SessionRequest, int, ILiveSession?> createSession,
        Func<string, string, CancellationToken, Task<LoadedPresentation>> loadPresentation,
        PresenterSettings? settings = null,
        TimeProvider? timeProvider = null,
        ToolRegistry? toolRegistry = null,
        Func<int, bool>? hasDelegationModel = null,
        Func<string, CancellationToken, Task<SessionToolSet>>? loadSessionTools = null,
        TimeSpan? startToolBudget = null,
        IScriptRevisionService? scriptRevisions = null)
    {
        _createSession = createSession ?? throw new ArgumentNullException(nameof(createSession));
        _loadPresentation = loadPresentation ?? throw new ArgumentNullException(nameof(loadPresentation));
        _settings = settings ?? new PresenterSettings();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _toolRegistry = toolRegistry ?? new ToolRegistry();
        _hasDelegationModel = hasDelegationModel;
        _loadSessionTools = loadSessionTools;
        _startToolBudget = startToolBudget ?? TimeSpan.FromSeconds(4);
        _scriptRevisions = scriptRevisions;
        if (_scriptRevisions is not null) _scriptRevisions.Changed += OnScriptRevisionsChanged;
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
    public event Action? Flush;
    public event Action<PresenterTranscript>? Transcript;
    public event Action<PresenterUsage>? Usage;
    public event Action<PresenterClosed>? Closed;
    public event Action<PresenterLog>? Log;
    public event Action<PresenterUpstreamError>? UpstreamError;
    public event Action<PresenterLimitWarning>? LimitWarning;
    public event Action<PresenterUpstreamStatus>? UpstreamStatus;

    public static int PartGapFor(int advanceSilenceMs) => Math.Min(PartGapMs, (int)Math.Round(advanceSilenceMs * 0.8));

    public static bool IsNormalClose(string? reason) => reason?.Contains("request", StringComparison.Ordinal) is true;

    public PresenterSnapshot Snapshot() => Volatile.Read(ref _snapshot);

    public async Task<PresenterStartResult> StartAsync(
        string id,
        int? fromIndex,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        return await StartAsync(id, fromIndex, ownerId, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PresenterStartResult> StartAsync(
        string id, int? fromIndex, string ownerId, int? maxMinutes, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var ticket = new StartTicket();
        // Registered before the command is written, so an End or abort from now on cancels this Start.
        lock (_ticketGate) _queuedStarts.Add(ticket);
        var command = new StartCommand(ownerId, id, fromIndex, maxMinutes, ticket);
        try
        {
            await WriteAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_ticketGate) _queuedStarts.Remove(ticket);
            throw;
        }
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

    public Task<bool> RequestEndConfirmationAsync(bool confirmed, CancellationToken cancellationToken = default) =>
        EnqueueCommandAsync(new ConfirmEndCommand(confirmed), cancellationToken);

    public Task<bool> ResumeAsync(CancellationToken cancellationToken = default) =>
        EnqueueCommandAsync(new ResumeCommand(), cancellationToken);

    public Task<bool> MuteAsync(CancellationToken cancellationToken = default) =>
        EnqueueCommandAsync(new MuteCommand(), cancellationToken);

    public Task<bool> UnmuteAsync(CancellationToken cancellationToken = default) =>
        EnqueueCommandAsync(new UnmuteCommand(), cancellationToken);

    public Task<bool> SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken = default) =>
        EnqueueCommandAsync(new SendAudioCommand(pcm16.ToArray()), cancellationToken);

    public Task<bool> EndAsync(bool resumable = false, CancellationToken cancellationToken = default) =>
        EndAsync(EndReasons.User, resumable, cancellationToken);

    public Task<bool> EndAsync(string endReason, bool resumable = false, CancellationToken cancellationToken = default) =>
        EndWithReasonAsync(endReason, resumable, cancellationToken);

    private Task<bool> EndWithReasonAsync(string endReason, bool resumable, CancellationToken cancellationToken)
    {
        CancelConnect(endReason);
        return EnqueueCommandAsync(new EndCommand(resumable, endReason), cancellationToken);
    }

    /// <summary>
    /// Cancels every queued Start and the current talk's load, connects and reconnects, and records
    /// <paramref name="reason"/> on each (first one wins per ticket). An End while idle with nothing queued touches
    /// only the finished talk's ticket, so it cannot label the next talk.
    /// </summary>
    private void CancelConnect(string? reason)
    {
        StartTicket[] tickets;
        lock (_ticketGate)
        {
            tickets = _talk is null ? [.. _queuedStarts] : [.. _queuedStarts, _talk];
            foreach (var ticket in tickets) ticket.RecordEndReason(reason);
        }
        // Cancel outside the gate: token callbacks may run loop continuations inline on this thread.
        foreach (var ticket in tickets) ticket.Cancel();
    }

    /// <summary>
    /// Cancels queued and in-flight Starts and connects without choosing an end reason. Callers end the talk next
    /// (bridge cleanup, shutdown, CLI cancellation); a Start enqueued after this call is unaffected.
    /// </summary>
    public void AbortPendingStart() => CancelConnect(null);

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

        // Cancels queued Starts and the current talk's load and connects. From here no Start or reconnect can create
        // an upstream: acceptance rejects a cancelled ticket and ConnectUpstreamAsync checks the ticket before every
        // candidate, so abandoning a loop wedged in a loader that ignores cancellation is safe.
        CancelConnect(EndReasons.Shutdown);
        CancelRun();
        if (_scriptRevisions is not null) _scriptRevisions.Changed -= OnScriptRevisionsChanged;

        var stopping = StopLoopAsync();
        try
        {
            await stopping.WaitAsync(ShutdownBound, _timeProvider).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LogMessage("warn", $"presenter loop did not stop within {ShutdownBound.TotalSeconds:0} s; abandoning it");
            _events.Writer.TryComplete();
            _lifetime.Cancel();
            _ = stopping.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            // The abandoned loop still reads _lifetime.Token and _runCts, so neither is disposed here.
            return;
        }

        _runCts?.Dispose();
        _lifetime.Dispose();
    }

    private async Task StopLoopAsync()
    {
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
                QueueFromProducer(new BackgroundFailure(operation, completed.Exception.GetBaseException().Message));
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
                try
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
                        await OnWrapUpFallbackAsync().ConfigureAwait(false);
                        break;
                    case QuestionHoldElapsed question when question.Generation == _questionGeneration:
                        _questionTimer = null;
                        OnQuestionHoldElapsed();
                        break;
                    case UtteranceElapsed gap when gap.Generation == _utteranceGeneration:
                        _utteranceTimer = null;
                        CompleteUtterance();
                        break;
                    case InteractionElapsed phase when phase.Generation == _interactionGeneration:
                        _interactionTimer = null;
                        OnInteractionElapsed();
                        break;
                    case PermitElapsed permit when permit.Generation == _permitGeneration:
                        ClosePermit();
                        break;
                    case GuardElapsed guard when ReferenceEquals(guard.Source, _guard) &&
                        guard.Generation == _guard.Generation:
                        await OnGuardElapsedAsync().ConfigureAwait(false);
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
                    case ApprovedToolCompleted approved:
                        OnApprovedToolCompleted(approved);
                        break;
                    case HostedActivityReceived hosted when ReferenceEquals(hosted.Session, _session):
                        OnHostedActivity(hosted);
                        break;
                    case SessionWarning warning:
                        if (ReferenceEquals(warning.Session, _session))
                        {
                            LogMessage("warn", warning.Message);
                        }

                        break;
                    case BackgroundFailure failure:
                        LogMessage("error", $"{failure.Operation} failed: {failure.Message}");
                        break;
                    case TrainingEvent training:
                        OnTrainingEvent(training);
                        break;
                    case Barrier barrier:
                        barrier.Completion.TrySetResult();
                        break;
                    case Shutdown shutdown:
                        try { await CloseForShutdownAsync().ConfigureAwait(false); }
                        finally { shutdown.Completion.TrySetResult(); }
                        return;
                }
                }
                catch (Exception exception)
                {
                    await FailSafeCloseAsync(exception).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            CancelRun();
            StopGuard();
            if (_session is { } session)
            {
                _session = null;
                await DisposeSessionAsync(session, "loop exit").ConfigureAwait(false);
            }
        }
    }

    private async Task CloseForShutdownAsync()
    {
        CancelRun();
        StopGuard();
        var session = _session;
        _session = null;
        LiveCloseResult? result = null;
        if (session is not null)
        {
            Exception? closeError = null;
            try { result = await session.CloseAsync().ConfigureAwait(false); }
            catch (Exception ex) { closeError = ex; }
            finally { await DisposeSessionAsync(session, "shutdown").ConfigureAwait(false); }
            if (closeError is not null) LogMessage("error", $"shutdown close failed: {closeError.GetType().Name}");
        }
        if (_state != PresenterState.Idle)
        {
            _talkEndedAt = _timeProvider.GetUtcNow();
            OnClosed("disposed", result?.Seconds, EndReasons.Shutdown);
        }
    }

    private async Task FailSafeCloseAsync(Exception exception)
    {
        try { LogMessage("error", $"presenter handler failed: {exception.GetType().Name}"); }
        catch (Exception) { }
        CancelRun();
        var session = _session;
        _session = null;
        LiveCloseResult? result = null;
        if (session is not null)
        {
            try { result = await session.CloseAsync().ConfigureAwait(false); }
            catch (Exception)
            {
                try { session.Terminate(); }
                catch (Exception) { }
            }
            finally { await DisposeSessionAsync(session, "handler failure").ConfigureAwait(false); }
        }
        if (_state != PresenterState.Idle)
        {
            _talkEndedAt = _timeProvider.GetUtcNow();
            try { OnClosed(exception.GetType().Name, result?.Seconds, EndReasons.Error); }
            catch (Exception)
            {
                _state = PresenterState.Idle;
                Volatile.Write(ref _snapshot, BuildSnapshot());
            }
        }
    }

    private async Task ProcessStartAsync(StartCommand command)
    {
        var ticket = command.Ticket;
        bool rejected;
        lock (_ticketGate)
        {
            _queuedStarts.Remove(ticket);
            rejected = ticket.Token.IsCancellationRequested || Volatile.Read(ref _disposed) != 0;
            // Only an accepted Start becomes the talk; a Start ignored because a talk is running must not
            // replace that talk's ticket. Under the gate, a signal sees the ticket either queued or as the talk.
            if (!rejected && _state == PresenterState.Idle) _talk = ticket;
        }
        if (rejected)
        {
            LogMessage("info", $"start cancelled before it began ({ticket.EndReason ?? "aborted"})");
            command.Completion.TrySetResult(new PresenterStartResult(false, command.Id, null, null, null));
            return;
        }
        try
        {
            command.Completion.TrySetResult(await StartAsyncCore(command.OwnerId, command.Id, command.FromIndex,
                command.MaxMinutes, ticket).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            await FailSafeCloseAsync(exception).ConfigureAwait(false);
            command.Completion.TrySetResult(new PresenterStartResult(false, command.Id, null, null, null));
        }
    }

    private async Task ProcessCommandAsync(Command command)
    {
        try
        {
            if (command is not (SendAudioCommand or EndCommand)) RecordActivity();
            if (command is NextCommand or PrevCommand or GotoCommand or PauseCommand or ResumeCommand or EndCommand)
                CancelToolConfirmation();
            var result = command switch
            {
                NextCommand next => await NavigateAfterReconnectAsync(() => NextCore(next.InvocationCallId)).ConfigureAwait(false),
                PrevCommand prev => await NavigateAfterReconnectAsync(() => PrevCore(prev.InvocationCallId)).ConfigureAwait(false),
                GotoCommand goTo => await NavigateAfterReconnectAsync(() => GotoCore(goTo.Index, goTo.InvocationCallId)).ConfigureAwait(false),
                PauseCommand => PauseCore(),
                ConfirmEndCommand confirm => await ConfirmEndCore(confirm.Confirmed).ConfigureAwait(false),
                ResumeCommand => await ResumeAfterReconnectAsync().ConfigureAwait(false),
                MuteCommand => MuteCore(),
                UnmuteCommand => UnmuteCore(),
                SendAudioCommand audio => SendAudioCore(audio.Pcm16),
                EndCommand end => await EndAsyncCore(end.Resumable, end.EndReason).ConfigureAwait(false),
                TrainerModeCommand trainer => SetTrainerModeCore(trainer.OwnerId, trainer.On),
                TrainOnTurnCommand turn => TrainOnTurnCore(turn.OwnerId, turn.Question, turn.Answer, turn.SlideIndex),
                _ => false
            };
            command.Completion.TrySetResult(result);
        }
        catch (Exception exception)
        {
            command.Completion.TrySetException(exception);
            await FailSafeCloseAsync(exception).ConfigureAwait(false);
        }
    }

    private async Task<PresenterStartResult> StartAsyncCore(string ownerId, string id, int? fromIndex, int? maxMinutes,
        StartTicket ticket)
    {
        if (_state != PresenterState.Idle)
        {
            LogMessage("warn", $"start ignored: state is {StateName(_state)}");
            return new PresenterStartResult(false, id, null, null, null);
        }

        ResetUtterance();
        SetInteraction(Interaction.None);
        _voicedIntervals.Clear();
        _lastVoicedAt = 0;
        _endResumable = false;
        _navigatingCallIds.Clear();
        _runGeneration++;
        _toolRoundTracker.Clear();
        _approvedTools.Clear();
        _hostedStarted.Clear();
        _requestedEndReason = null;
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        _talkStartedAt = _timeProvider.GetUtcNow();
        _talkEndedAt = null;
        _priorUsageSeconds = 0;
        _priorEstimatedSeconds = 0;
        _usageConfirmed = true;
        StopGuard();
        var initialCap = Math.Min(_settings.MaxTalkMinutes, _settings.MaxTalkCeilingMinutes);
        if (maxMinutes is >= 5) initialCap = Math.Min(initialCap, maxMinutes.Value);
        TalkGuard? guard = null;
        guard = new TalkGuard(_timeProvider, initialCap, _settings.IdleTimeoutSeconds,
            _settings.PauseGraceSeconds, generation => QueueFromProducer(new GuardElapsed(guard!, generation)),
            () =>
            {
                if (ReferenceEquals(Volatile.Read(ref _guard), guard)) ticket.Cancel(EndReasons.MaxLength);
            });
        Volatile.Write(ref _guard, guard);
        _talkStartedAt = guard.StartedAt;
        _suspended = false;
        _endDiagnostic = null;
        ResetTrainingForStart(ownerId);
        SetState(PresenterState.Connecting);
        // The ticket was cancellable since StartAsync enqueued it, so there is no check-then-publish gap: an End or
        // abort before, during or after acceptance is already visible here.
        using var startCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, ticket.Token);
        using var toolsCts = CancellationTokenSource.CreateLinkedTokenSource(startCts.Token);
        var toolsStartedAt = _timeProvider.GetTimestamp();
        Task<SessionToolSet>? toolsTask = null;
        if (_loadSessionTools is not null && (_hasDelegationModel is null || Enumerable.Range(0, MaxUpstreamAttempts).Any(i => _hasDelegationModel(i))))
        {
            try { toolsTask = _loadSessionTools(ownerId, toolsCts.Token); }
            catch (Exception ex) { LogMessage("warn", $"tools: load failed ({ex.GetType().Name})"); }
        }
        var presentationTask = _loadPresentation(ownerId, id, startCts.Token);
        try
        {
            _presentation = await presentationTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (startCts.IsCancellationRequested)
        {
            toolsCts.Cancel();
            if (toolsTask is not null) ObserveLateToolSet(toolsTask);
            await EndAsyncCore(false, _talk?.EndReason ?? EndReasons.Shutdown).ConfigureAwait(false);
            return new PresenterStartResult(false, id, null, null, null);
        }
        catch (Exception exception)
        {
            LogMessage("error", $"cannot load presentation \"{id}\": {exception.Message}");
            UpstreamError?.Invoke(new PresenterUpstreamError($"cannot load presentation: {exception.Message}", "presentation"));
            toolsCts.Cancel();
            if (toolsTask is not null) ObserveLateToolSet(toolsTask);
            SetState(PresenterState.Idle);
            StopGuard();
            CancelRun();
            return new PresenterStartResult(false, id, null, null, null);
        }

        var scriptCap = _presentation.Meta.MaxMinutes ?? _settings.MaxTalkMinutes;
        if (scriptCap > _settings.MaxTalkCeilingMinutes)
            LogMessage("warn", $"limit: clamped {scriptCap} → {_settings.MaxTalkCeilingMinutes} min");
        if (maxMinutes > _settings.MaxTalkCeilingMinutes)
            LogMessage("warn", $"limit: clamped override {maxMinutes} → {_settings.MaxTalkCeilingMinutes} min");
        var effectiveCap = Math.Min(scriptCap, _settings.MaxTalkCeilingMinutes);
        if (maxMinutes is >= 5) effectiveCap = Math.Min(effectiveCap, maxMinutes.Value);
        _guard.Tighten(effectiveCap);
        LogMessage("info", $"limit: max length {effectiveCap} min (script {scriptCap}, override {maxMinutes?.ToString() ?? "none"}, ceiling {_settings.MaxTalkCeilingMinutes})");
        if (_guard.MaxExpired)
        {
            await EndAsyncCore(false, EndReasons.MaxLength).ConfigureAwait(false);
            return new PresenterStartResult(false, id, null, null, null);
        }

        if (toolsTask is not null)
        {
            var remaining = _startToolBudget - _timeProvider.GetElapsedTime(toolsStartedAt);
            if (remaining > TimeSpan.Zero && await Task.WhenAny(toolsTask, Task.Delay(remaining, startCts.Token))
                .ConfigureAwait(false) == toolsTask)
            {
                try
                {
                    _sessionTools = await toolsTask.ConfigureAwait(false);
                    foreach (var note in _sessionTools.Notes) LogMessage("info", note);
                }
                catch (Exception ex)
                {
                    LogMessage("warn", $"tools: load failed ({ex.GetType().Name})");
                }
            }
            else
            {
                toolsCts.Cancel();
                LogMessage("warn", "tools: start budget exceeded");
                ObserveLateToolSet(toolsTask);
            }
        }
        if (startCts.IsCancellationRequested)
        {
            await EndAsyncCore(false, _talk?.EndReason ?? EndReasons.Shutdown).ConfigureAwait(false);
            return new PresenterStartResult(false, id, null, null, null);
        }
        _catalogue = ToolSessionCatalogue.Build(_toolRegistry, _sessionTools?.Tools, _settings.MaxInlineTools);
        foreach (var note in _catalogue.Notes) LogMessage("info", note);
        var presentation = _presentation;
        var connection = await ConnectUpstreamAsync().ConfigureAwait(false);
        if (connection.Cancelled)
        {
            await EndAsyncCore(false, _talk?.EndReason ?? EndReasons.MaxLength).ConfigureAwait(false);
            return new PresenterStartResult(false, id, null, null, null);
        }
        if (connection.Session is null || connection.Info is null)
        {
            LogMessage("error", "no upstream could start a session");
            ReleaseSessionTools();
            SetState(PresenterState.Idle);
            StopGuard();
            CancelRun();
            return new PresenterStartResult(false, id, null, null, null);
        }

        var session = connection.Session;
        var sessionInfo = connection.Info;
        _session = session;
        _sessionInfo = sessionInfo;
        try
        {
            _connectedAt = _timeProvider.GetUtcNow();
            OnTrainingTalkConnected(ownerId, ticket);
            if (sessionInfo.DelegationMode == "client")
            {
                if (_sessionTools is not null)
                {
                    ReleaseSessionTools();
                    _catalogue = ToolSessionCatalogue.Build(_toolRegistry, maxInlineTools: _settings.MaxInlineTools);
                    LogMessage("info", "external tools need a delegation model; not used in this talk");
                }
                session.AppendInstructions(PromptBuilder.ClientModeInstruction(), "client-mode-controls");
            }

            var startAt = fromIndex
                ?? (_lastRun is { EndedNormally: false } last && last.Id == id ? last.Index : 0);
            startAt = Math.Clamp(startAt, 0, Math.Max(0, presentation.Slides.Count - 1));
            _muted = false;
            _usageSeconds = 0;
            _usageRatio = null;
            SetState(PresenterState.Presenting);
            _guard?.StartPresenting();
            PresentSlide(startAt, interrupt: false);
            OnTrainingSessionReady(reconnected: false);
            return new PresenterStartResult(true, id, connection.Label, sessionInfo.Id, sessionInfo.Model,
                _connectedAt);
        }
        catch (Exception exception)
        {
            await FailSafeCloseAsync(exception).ConfigureAwait(false);
            return new PresenterStartResult(false, id, null, null, null);
        }
    }

    private async Task<ConnectResult> ConnectUpstreamAsync()
    {
        var presentation = _presentation!;
        var hasExternalTools = _sessionTools is { Tools.Count: > 0 } or { HostedTools.Count: > 0 };
        // Start and every reconnect run under the talk's ticket, which off-loop End, abort and the guard cancel
        // directly. Nothing is published here, so a signal that arrives before this line is still observed below,
        // before any candidate upstream is created.
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token,
            _talk?.Token ?? CancellationToken.None);
        for (var attempt = 0; attempt < MaxUpstreamAttempts; attempt++)
        {
            if (connectCts.IsCancellationRequested || _guard?.MaxExpired == true)
                return new ConnectResult(null, null, null, true);
            var isManaged = _hasDelegationModel?.Invoke(attempt) ?? true;
            var instructions = PromptBuilder.SystemInstructions(presentation.Meta.Title, presentation.Slides,
                presentation.Context, onWarn: message => LogMessage("warn", message), managedMode: isManaged) +
                (isManaged && hasExternalTools ? PromptBuilder.ExternalToolsSystemRules() : "");
            var inlineTools = isManaged ? _catalogue?.GetInlineToolDefinitions() : null;
            var delegationInstructions = isManaged
                ? PromptBuilder.BackendInstructions(presentation.Meta.Title, presentation.Slides) +
                  (hasExternalTools ? PromptBuilder.ExternalToolsBackendRules() : "") : null;
            var request = new SessionRequest(instructions, presentation.Meta.Voice ?? _settings.Voice,
                presentation.Meta.Title, inlineTools, delegationInstructions,
                isManaged ? _sessionTools?.HostedTools : null);
            // Building the request runs host callbacks; re-check so a signal raised meanwhile creates no candidate.
            if (connectCts.IsCancellationRequested || _guard?.MaxExpired == true)
                return new ConnectResult(null, null, null, true);
            var candidate = _createSession(request, attempt);
            if (candidate is null) break;
            var label = candidate.Name ?? $"upstream #{attempt + 1}";
            try
            {
                WireSession(candidate);
                var info = await candidate.ConnectAsync(connectCts.Token).ConfigureAwait(false);
                if (connectCts.IsCancellationRequested || _guard?.MaxExpired == true)
                {
                    await DisposeSessionAsync(candidate, "cancelled connect").ConfigureAwait(false);
                    return new ConnectResult(null, null, null, true);
                }
                LogMessage(attempt > 0 ? "warn" : "info", $"connected via {label}");
                return new ConnectResult(candidate, info, label, false);
            }
            catch (OperationCanceledException) when (connectCts.IsCancellationRequested || _guard?.MaxExpired == true)
            {
                await DisposeSessionAsync(candidate, "cancelled connect").ConfigureAwait(false);
                return new ConnectResult(null, null, null, true);
            }
            catch (Exception exception)
            {
                _endDiagnostic = exception.Message;
                await DisposeSessionAsync(candidate, $"failed start via {label}").ConfigureAwait(false);
                LogMessage("error", $"session start via {label} failed: {exception.Message}");
                var startup = exception as LiveStartupException;
                UpstreamError?.Invoke(new PresenterUpstreamError($"{label}: {exception.Message}",
                    startup?.Code ?? "connect"));
                if (connectCts.IsCancellationRequested || _guard?.MaxExpired == true)
                    return new ConnectResult(null, null, null, true);
            }
        }
        return new ConnectResult(null, null, null, false);
    }

    private sealed record ConnectResult(ILiveSession? Session, LiveSessionInfo? Info, string? Label, bool Cancelled);

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
        session.HostedToolActivity += (delegationId, type, status) => QueueFromProducer(new HostedActivityReceived(session, delegationId, type, status));
        session.Closed += (reason, seconds) => QueueFromProducer(new SessionClosed(session, reason, seconds));
    }

    private void PresentSlide(int index, bool interrupt, string? lead = null)
    {
        if (_presentation is null || index < 0 || index >= _presentation.Slides.Count)
        {
            return;
        }

        CompleteSlideDiagnostics();
        ClearTimers();
        _slideIndex = index;
        RecordActivity();
        _heardOutput = false;
        _nudgeCount = 0;
        _wrappingUp = false;
        StartSlideDiagnostics();
        _replayOnResume = false;
        _replayLead = null;
        var slide = _presentation.Slides[index];
        LogMessage("info", $"slide {index + 1}/{SlideCount}{(slide.Title.Length > 0 ? $" — {slide.Title}" : string.Empty)}");
        Slide?.Invoke(index);
        PublishSnapshot();
        if (NarrationHeld)
        {
            PresentHeldSlide(index);
            return;
        }

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

        SendNextPart(interrupt, lead);
        ArmNudge();
    }

    private bool SendNextPart(bool interrupt = false, string? lead = null)
    {
        if (_presentation is null || _partsSent >= _parts.Count)
        {
            return false;
        }

        var slide = _presentation.Slides[_slideIndex];
        var part = _partsSent;
        _session?.AppendInstructions(
            PromptBuilder.SlideInstruction(_slideIndex, SlideCount, slide.Title, _parts[part], part + 1, _parts.Count, interrupt && part == 0,
                part == 0 ? lead : null),
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

        var voiced = AudioLevel.IsVoiced(audio.Bytes);
        var forwarded = _state == PresenterState.Presenting || (_state == PresenterState.Paused && _speechPermit &&
            (_permitBarrierMs is null || audio.StartMs is null || audio.StartMs >= _permitBarrierMs));
        if (forwarded)
        {
            Audio?.Invoke(new PresenterAudio(audio.Bytes, audio.StartMs, audio.EndMs));
        }

        if (!forwarded)
        {
            return;
        }

        if (voiced)
        {
            if (_state == PresenterState.Presenting) RecordActivity();
            _lastVoicedAt = _timeProvider.GetTimestamp();
            if (audio.StartMs is { } start && audio.EndMs is { } end)
            {
                _voicedIntervals.Enqueue((start, end));
                while (_voicedIntervals.Count > 256)
                {
                    _voicedIntervals.Dequeue();
                }
            }

            if (_interaction == Interaction.AwaitingConfirmQuestion)
            {
                _endQuestionVoiced = true;
                ArmInteraction(500);
            }
            if (_state == PresenterState.Paused)
            {
                ArmPermitQuiet();
                if (_interaction == Interaction.AwaitingEndQuestion)
                {
                    _endQuestionVoiced = true;
                    ArmInteraction(500);
                }
            }
        }
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
            if (_pendingTool is null && _questionHoldOpen && _interaction != Interaction.WaitingOnSlide && !_toolRoundTracker.HasPendingBackendDelegation && IsAfterLatestQuestion(audio))
            {
                if (!_answerVoiced)
                {
                    _answerVoiced = true;
                    SetInteraction(Interaction.Answering);
                    // The follow-up window takes over from here; the 15 s timer must not cut a long answer short.
                    ClearQuestionTimer();
                    var elapsed = (long)_timeProvider.GetElapsedTime(_questionOpenedAt).TotalMilliseconds;
                    LogMessage("info", $"question: answered after {elapsed} ms");
                }
            }

            if (_pendingTool is not null) return;
            if (_questionHoldOpen && _interaction != Interaction.WaitingOnSlide && _answerVoiced)
            {
                _lastAnswerAt = _timeProvider.GetTimestamp();
                SetInteraction(Interaction.Answering);
                ArmInteraction(700);
            }
            else if (_interaction != Interaction.WaitingOnSlide)
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
        RecordRecentTurn(transcript.Role, transcript.Delta);
        if (transcript.Role == "user" && !string.IsNullOrWhiteSpace(transcript.Delta)) RecordActivity();
        if (transcript.Role == "user" && _slideDiagnosticsActive)
        {
            _userTranscriptCharacters += transcript.Delta.Length;
        }

        if (transcript.Role == "user" && (_state is PresenterState.Presenting or PresenterState.Paused) && !string.IsNullOrWhiteSpace(transcript.Delta))
        {
            if (_state == PresenterState.Presenting)
            {
                if (_interaction == Interaction.WaitingOnSlide)
                {
                    if (_utterance.Length == 0)
                    {
                        _rangeReply = false;
                        // Remember the waiting phase for a resume command after the transcript opens its hold.
                        _utteranceBeganDuringWaiting = true;
                    }
                    SetInteraction(Interaction.None);
                }
                OpenOrExtendQuestionHold(transcript.EndMs);
            }

            AppendUtterance(transcript);
        }
    }

    private void AppendUtterance(TranscriptReceived transcript)
    {
        if (_utterance.Length == 0)
        {
            _utteranceStartMs = transcript.StartMs;
            _utteranceOpenedAt = _timeProvider.GetTimestamp();
            _utteranceDuringSpeech = false;
            _utteranceBeganDuringCarryOn = _interaction == Interaction.AwaitingCarryOn;
        }

        _utterance += transcript.Delta;
        _utteranceEndMs = transcript.EndMs;
        _utteranceTooLong |= _utterance.Length > 120 || _timeProvider.GetElapsedTime(_utteranceOpenedAt).TotalMilliseconds > 6000
            || (_utteranceStartMs is { } start && _utteranceEndMs is { } end && end - start > 6000);
        _utteranceDuringSpeech |= DuringSpeech(transcript.StartMs, transcript.EndMs);
        _utteranceTimer?.Dispose();
        var generation = ++_utteranceGeneration;
        _utteranceTimer = _timeProvider.CreateTimer(
            _ => QueueFromProducer(new UtteranceElapsed(generation)), null,
            TimeSpan.FromMilliseconds(700), Timeout.InfiniteTimeSpan);
    }

    private bool DuringSpeech(long? start, long? end)
    {
        if (start is { } s && end is { } e)
        {
            return _voicedIntervals.Any(interval => interval.Start <= e && interval.End >= s);
        }

        return _lastVoicedAt != 0 && _timeProvider.GetElapsedTime(_lastVoicedAt).TotalMilliseconds <= 300;
    }

    private void ResetUtterance()
    {
        _utteranceTimer?.Dispose();
        _utteranceTimer = null;
        _utteranceGeneration++;
        _utterance = "";
        _utteranceStartMs = null;
        _utteranceEndMs = null;
        _utteranceTooLong = false;
        _utteranceDuringSpeech = false;
        _utteranceBeganDuringCarryOn = false;
        _utteranceBeganDuringWaiting = false;
    }

    private void CompleteUtterance()
    {
        if (_utterance.Length == 0)
        {
            return;
        }

        var phrase = _utterance;
        var newQuestionDuringCarryOn = _utteranceBeganDuringCarryOn;
        var beganDuringWaiting = _utteranceBeganDuringWaiting;
        var end = _utteranceEndMs;
        var speaking = _utteranceDuringSpeech || DuringSpeech(_utteranceStartMs, end);
        var command = _utteranceTooLong || _timeProvider.GetElapsedTime(_utteranceOpenedAt).TotalMilliseconds > 6000
            || (_utteranceStartMs is { } start && end is { } finish && finish - start > 6000)
            ? null : VoiceCommandMatcher.Match(phrase);
        ResetUtterance();
        if (command is not null && speaking && command.Intent != VoiceCommandIntent.Pause)
        {
            LogMessage("info", $"voice: \"{phrase.Trim()}\" ignored (model speaking)");
            command = null;
        }

        if (command is { Intent: VoiceCommandIntent.GoTo } &&
            (command.SlideNumber < 1 || command.SlideNumber > SlideCount))
        {
            if (_state == PresenterState.Presenting)
            {
                OpenOrExtendQuestionHold(end);
                _rangeReply = true;
            }
            else OpenPermit(end);
            LogMessage("info", $"voice: go to slide {command.SlideNumber} out of range (1-{SlideCount})");
            _session?.AppendInstructions(PromptBuilder.InvalidSlideRangeInstruction(SlideCount), "invalid-slide-range");
            return;
        }

        var keepHold = command is { Intent: VoiceCommandIntent.No } && _interaction == Interaction.AwaitingCarryOn;
        if (command is not null && ExecuteVoiceCommand(command, beganDuringWaiting))
        {
            if (!keepHold && _interaction is not (Interaction.AwaitingConfirmQuestion or Interaction.AwaitingConfirmAnswer)) ClearQuestionHold();
            LogMessage("info", $"voice: {command.Intent.ToString().ToLowerInvariant()} (instant)");
            return;
        }

        if (_interaction is Interaction.AwaitingConfirmQuestion or Interaction.AwaitingConfirmAnswer)
        {
            return;
        }

        if (_state == PresenterState.Paused)
        {
            OpenPermit(end);
        }
        else if (newQuestionDuringCarryOn && _interaction == Interaction.AwaitingCarryOn)
        {
            SetInteraction(Interaction.None);
            OpenOrExtendQuestionHold(end);
        }
    }

    private bool ExecuteVoiceCommand(VoiceCommand command, bool beganDuringWaiting = false)
    {
        switch (command.Intent)
        {
            case VoiceCommandIntent.Pause:
                CancelToolConfirmation();
                return PauseCore();
            case VoiceCommandIntent.Resume:
                CancelToolConfirmation();
                if (_suspended) { ObserveBackground(ResumeAsync(), "voice reconnect"); return true; }
                return ResumeCore(beganDuringWaiting);
            case VoiceCommandIntent.Next:
                CancelToolConfirmation();
                if (_suspended) { ObserveBackground(NextAsync(), "voice next reconnect"); return true; }
                return NextCore();
            case VoiceCommandIntent.Previous:
                CancelToolConfirmation();
                if (_suspended) { ObserveBackground(PrevAsync(), "voice previous reconnect"); return true; }
                return PrevCore();
            case VoiceCommandIntent.GoTo:
                CancelToolConfirmation();
                if (_suspended)
                {
                    ObserveBackground(GotoAsync(command.SlideNumber!.Value - 1), "voice goto reconnect");
                    return true;
                }
                return GotoCore(command.SlideNumber!.Value - 1);
            case VoiceCommandIntent.End:
                CancelToolConfirmation();
                StartEndConfirmation();
                return true;
            case VoiceCommandIntent.Yes when _interaction == Interaction.AwaitingCarryOn:
                SetInteraction(Interaction.None);
                ResumeAfterQuestion("question: confirmed; resuming");
                return true;
            case VoiceCommandIntent.No when _interaction == Interaction.AwaitingCarryOn:
                SetInteraction(Interaction.WaitingOnSlide);
                ClearQuestionTimer();
                ClearSilenceTimer();
                return true;
            case VoiceCommandIntent.Yes when _interaction == Interaction.AwaitingConfirmAnswer:
                ApproveToolConfirmation();
                return true;
            case VoiceCommandIntent.No when _interaction == Interaction.AwaitingConfirmAnswer:
                CancelToolConfirmation("declined");
                return true;
            case VoiceCommandIntent.Yes when _interaction == Interaction.AwaitingEndAnswer:
                ObserveBackground(EndAsync(), "voice confirmed end");
                return true;
            case VoiceCommandIntent.No when _interaction == Interaction.AwaitingEndAnswer:
                return ResumeCore();
            default:
                return false;
        }
    }

    private void OpenPermit(long? barrier)
    {
        _speechPermit = true;
        _permitBarrierMs = barrier;
        ArmPermitQuiet();
    }

    private void ArmPermitQuiet()
    {
        _permitTimer?.Dispose();
        var generation = ++_permitGeneration;
        _permitTimer = _timeProvider.CreateTimer(
            _ => QueueFromProducer(new PermitElapsed(generation)), null,
            TimeSpan.FromMilliseconds(1500), Timeout.InfiniteTimeSpan);
    }

    private void ClosePermit()
    {
        _permitTimer?.Dispose();
        _permitTimer = null;
        _permitGeneration++;
        _speechPermit = false;
        _permitBarrierMs = null;
    }

    private void SetInteraction(Interaction interaction)
    {
        _interactionTimer?.Dispose();
        _interactionTimer = null;
        _interactionGeneration++;
        _interaction = interaction;
    }

    private void ArmInteraction(int milliseconds)
    {
        _interactionTimer?.Dispose();
        var generation = ++_interactionGeneration;
        _interactionTimer = _timeProvider.CreateTimer(
            _ => QueueFromProducer(new InteractionElapsed(generation)), null,
            TimeSpan.FromMilliseconds(milliseconds), Timeout.InfiniteTimeSpan);
    }

    private void OnInteractionElapsed()
    {
        switch (_interaction)
        {
            case Interaction.Answering:
                if (_rangeReply)
                {
                    SetInteraction(Interaction.WaitingOnSlide);
                    break;
                }
                SetInteraction(Interaction.AwaitingCarryOn);
                var remaining = FollowUpWaitMs + 700 - (int)_timeProvider.GetElapsedTime(_lastAnswerAt).TotalMilliseconds;
                if (remaining <= 0)
                {
                    OnInteractionElapsed();
                }
                else
                {
                    ArmInteraction(remaining);
                }

                break;
            case Interaction.AwaitingCarryOn:
                SetInteraction(Interaction.None);
                ResumeAfterQuestion($"question: no follow-up after {FollowUpWaitMs} ms; resuming");
                break;
            case Interaction.AwaitingConfirmQuestion:
                if (!_endQuestionVoiced) LogMessage("warn", "tool: confirmation question not voiced after 8 s");
                SetInteraction(Interaction.AwaitingConfirmAnswer);
                ArmInteraction(10_000);
                break;
            case Interaction.AwaitingConfirmAnswer:
                CancelToolConfirmation("not confirmed");
                break;
            case Interaction.AwaitingEndQuestion:
                if (!_endQuestionVoiced)
                {
                    LogMessage("warn", "end: question not voiced after 8 s");
                }

                SetInteraction(Interaction.AwaitingEndAnswer);
                ArmInteraction(10_000);
                break;
            case Interaction.AwaitingEndAnswer:
                LogMessage("info", "end: confirmation timed out; staying paused");
                SetInteraction(Interaction.None);
                ClosePermit();
                break;
        }
    }

    private void StartEndConfirmation()
    {
        if (_interaction is Interaction.AwaitingEndQuestion or Interaction.AwaitingEndAnswer)
        {
            return;
        }

        if (_state == PresenterState.Presenting)
        {
            PauseCore();
        }

        if (_state != PresenterState.Paused)
        {
            return;
        }

        ClearQuestionHold();
        _endQuestionVoiced = false;
        SetInteraction(Interaction.AwaitingEndQuestion);
        OpenPermit(null);
        _session?.AppendInstructions(PromptBuilder.EndConfirmationInstruction(), "end-confirmation");
        ArmInteraction(8000);
    }

    private async Task<bool> ConfirmEndCore(bool confirmed)
    {
        if (confirmed && _interaction == Interaction.AwaitingEndAnswer)
        {
            return await EndAsyncCore(false, EndReasons.User).ConfigureAwait(false);
        }

        StartEndConfirmation();
        return _state == PresenterState.Paused;
    }

    private void OnUsage(UsageReceived usage)
    {
        if (!ReferenceEquals(usage.Session, _session))
        {
            return;
        }

        _usageSeconds = _priorUsageSeconds + usage.Seconds;
        _usageRatio = usage.Ratio;
        Usage?.Invoke(new PresenterUsage(_usageSeconds, usage.Ratio));
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
                if (_questionHoldOpen) ArmQuestionHold();
                if (_toolRoundTracker.ShouldSendContinueResponses())
                {
                    TryContinueResponses();
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
        if (!ReferenceEquals(call.Session, _session) || _state is not (PresenterState.Presenting or PresenterState.Paused))
        {
            LogMessage("info", $"tool call for {call.CallId} dropped: session replaced or run not live");
            return;
        }

        RecordActivity();

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

        if (_questionHoldOpen) ArmQuestionHold();
        LogMessage("info", $"tool: {call.Name} ({call.CallId}) requested for delegation {call.DelegationId}");
        ToolResolution? resolution = null;
        try
        {
            using var doc = JsonDocument.Parse(call.Arguments);
            resolution = _catalogue?.Resolve(call.Name, doc.RootElement);
        }
        catch (JsonException) { }
        EditIntent? intent = null;
        if (resolution?.Tool is { } resolved && IsReviseScript(resolved) &&
            (intent = GateReviseScript(call, resolution.Arguments)) is null)
        {
            return;
        }

        if (resolution?.Tool is { RequiresConfirmation: true } tool)
        {
            var key = tool.Name + ":" + Canonicalize(resolution.Arguments).ToJsonString();
            if (_approvedTools.TryGetValue(key, out var approved) && _timeProvider.GetElapsedTime(approved.At).TotalSeconds < 60)
            {
                CompleteImmediateCall(call, approved.Result ?? ToolResult.Failure("running") with { Outcome = "running" });
                return;
            }
            if (_pendingTool is { } pending)
            {
                CompleteImmediateCall(call, ToolResult.Failure(pending.Key == key ? "confirmation_pending" : "another action is waiting for confirmation") with { Outcome = pending.Key == key ? "confirmation_pending" : "error" });
                return;
            }
            _pendingTool = new PendingToolConfirmation(key, tool, resolution.Arguments, call.Session, _runGeneration, intent);
            _endQuestionVoiced = false;
            SetInteraction(Interaction.AwaitingConfirmQuestion);
            OpenPermit(null);
            var question = tool.ConfirmationQuestion ?? $"Shall I use {tool.Title} on {tool.Source}?";
            CompleteImmediateCall(call, ToolResult.Failure(question) with { Outcome = "confirmation_required", Data = new JsonObject { ["status"] = "confirmation_required", ["question"] = question } });
            LogMessage("info", $"tool: {tool.Source}.{tool.Name} waiting for yes");
            ArmInteraction(8000);
            return;
        }
        StartToolInvocation(call.Session, _runGeneration, call.DelegationId, call.CallId, call.Name, call.Arguments, resolution?.Tool);
    }

    private void StartToolInvocation(
        ILiveSession session,
        long runGeneration,
        string delegationId,
        string callId,
        string name,
        string argumentsJson,
        ITool? effectiveTool = null)
    {
        var startedAt = _timeProvider.GetTimestamp();
        var catalogue = _catalogue;
        var runToken = _runCts?.Token ?? CancellationToken.None;
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
                                result = await InvokeBoundedAsync(resolution.Tool!, resolution.Arguments, runToken)
                                    .ConfigureAwait(false);
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                result = ToolResult.Failure("tool failed");
            }

            QueueFromProducer(new ToolInvocationCompleted(session, runGeneration, delegationId, callId, result,
                effectiveTool?.Source, effectiveTool?.Name, startedAt));
        });
    }

    // Bounded even when a tool ignores its token: the run gives up at the tool's timeout, works on a copy of the
    // arguments, and a fault that arrives after it gave up is logged by type only.
    private async Task<ToolResult> InvokeBoundedAsync(ITool tool, JsonElement arguments, CancellationToken runToken)
    {
        var abandoned = 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        cts.CancelAfter(tool.Timeout);
        var invocation = tool.InvokeAsync(arguments.Clone(), cts.Token);
        _ = invocation.ContinueWith(t =>
            {
                var fault = t.Exception!.GetBaseException();
                if (Volatile.Read(ref abandoned) == 1) QueueFromProducer(new BackgroundFailure("late tool fault", fault.GetType().Name));
            },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        try
        {
            return await invocation.WaitAsync(tool.Timeout, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested)
        {
            Volatile.Write(ref abandoned, 1);
            return ToolResult.Failure("cancelled") with { Outcome = "cancelled" };
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && cts.IsCancellationRequested))
        {
            Volatile.Write(ref abandoned, 1);
            return ToolResult.Failure("timed out") with { Outcome = "timeout" };
        }
    }

    private void OnToolInvocationCompleted(ToolInvocationCompleted completed)
    {
        RecordActivity();
        if (!ReferenceEquals(completed.Session, _session) || _state is not (PresenterState.Presenting or PresenterState.Paused))
        {
            LogMessage("info", $"tool invocation for {completed.CallId} dropped: session replaced or run not live");
            return;
        }

        var result = completed.RunGeneration != _runGeneration &&
            (!_navigatingCallIds.TryGetValue(completed.CallId, out var navigationGeneration) || navigationGeneration != _runGeneration)
            ? ToolResult.Failure("stale: the presentation moved on") : completed.Result;

        if (!_toolRoundTracker.IsCallPending(completed.DelegationId, completed.CallId))
        {
            LogMessage("info", $"tool invocation for {completed.CallId} dropped: call not pending in tracker");
            return;
        }

        if (_session?.SubmitToolOutput(completed.CallId, result.ToJsonString()) != true)
        {
            LogMessage("warn", $"tool: {completed.CallId} output refused");
            return;
        }
        _toolRoundTracker.MarkCallSubmitted(completed.DelegationId, completed.CallId);
        _navigatingCallIds.Remove(completed.CallId);
        if (completed.ToolSource is { } source && source != "presenter")
            LogMessage("info", $"tool: {source}.{completed.ToolName} {result.Outcome} {(int)_timeProvider.GetElapsedTime(completed.StartedAt).TotalMilliseconds} ms");
        else
            LogMessage("info", $"tool: {completed.CallId} output submitted ({result.Outcome})");
        TryContinueResponses();
    }

    private void TryContinueResponses()
    {
        if (_toolRoundTracker.ShouldSendContinueResponses() && _session?.ContinueResponses() == true)
        {
            _toolRoundTracker.OnResponsesContinued();
        }
    }

    private static JsonNode Canonicalize(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => new JsonObject(element.EnumerateObject()
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create<string, JsonNode?>(p.Name, Canonicalize(p.Value)))),
        JsonValueKind.Array => new JsonArray(element.EnumerateArray().Select(Canonicalize).ToArray()),
        _ => JsonNode.Parse(element.GetRawText())!
    };

    private void CompleteImmediateCall(ToolCallReceived call, ToolResult result) =>
        OnToolInvocationCompleted(new ToolInvocationCompleted(call.Session, _runGeneration, call.DelegationId, call.CallId, result));

    private void CancelToolConfirmation(string? reason = null)
    {
        if (_pendingTool is not { } pending) return;
        _pendingTool = null;
        SetInteraction(Interaction.None);
        ClosePermit();
        if (reason is not null) LogMessage("info", $"tool: {pending.Tool.Source}.{pending.Tool.Name} {reason}");
        if (reason is not null && pending.Intent is not null) OnScriptEditDeclined();
    }

    private void ApproveToolConfirmation()
    {
        if (_pendingTool is not { } pending) return;
        _pendingTool = null;
        SetInteraction(Interaction.None);
        ClosePermit();
        var startedAt = _timeProvider.GetTimestamp();
        // A script edit is enqueued here, on the loop, from the intent captured with the question; the tool run below
        // only produces the acknowledgement.
        if (pending.Intent is { } intent && !ApproveScriptEdit(intent)) return;
        var runToken = _runCts?.Token ?? CancellationToken.None;
        _approvedTools[pending.Key] = new ApprovedToolCall(startedAt, null);
        _ = Task.Run(async () =>
        {
            ToolResult result;
            try { result = await InvokeBoundedAsync(pending.Tool, pending.Arguments, runToken).ConfigureAwait(false); }
            catch (Exception) { result = ToolResult.Failure("tool failed"); }
            QueueFromProducer(new ApprovedToolCompleted(pending, result, startedAt));
        });
    }

    private void OnApprovedToolCompleted(ApprovedToolCompleted completed)
    {
        var pending = completed.Pending;
        if (ReferenceEquals(pending.Session, _session) && pending.Generation == _runGeneration)
        {
            _approvedTools[pending.Key] = new ApprovedToolCall(_timeProvider.GetTimestamp(), completed.Result);
            // A script edit was acknowledged when it was enqueued; its tool result is not external data.
            if (pending.Intent is null)
            {
                _session?.AppendCommentary($"Tool {pending.Tool.Name} {(completed.Result.Ok ? "succeeded" : "failed")}. External data: {completed.Result.Message[..Math.Min(300, completed.Result.Message.Length)]}");
                _session?.AppendThinking($"Untrusted external data: {completed.Result.ToJsonString()[..Math.Min(1200, completed.Result.ToJsonString().Length)]}");
            }
            LogMessage("info", $"tool: {pending.Tool.Source}.{pending.Tool.Name} {completed.Result.Outcome} {(int)_timeProvider.GetElapsedTime(completed.StartedAt).TotalMilliseconds} ms");
        }
        else
        {
            LogMessage("info", $"tool: {pending.Tool.Source}.{pending.Tool.Name} {completed.Result.Outcome} {(int)_timeProvider.GetElapsedTime(completed.StartedAt).TotalMilliseconds} ms (not announced: the talk moved on)");
        }
    }

    private void ObserveLateToolSet(Task<SessionToolSet> task) => ObserveBackground(Task.Run(async () =>
    {
        try { var set = await task.ConfigureAwait(false); await set.DisposeAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogMessage("warn", $"tools: load failed ({ex.GetType().Name})"); }
    }), "late tool set");

    private void OnHostedActivity(HostedActivityReceived activity)
    {
        if (activity.Type != "web_search") return;
        if (activity.Status is "in_progress" or "started")
            _hostedStarted[activity.DelegationId] = _timeProvider.GetTimestamp();
        else
        {
            var elapsed = _hostedStarted.Remove(activity.DelegationId, out var started)
                ? $" {(int)_timeProvider.GetElapsedTime(started).TotalMilliseconds} ms" : "";
            LogMessage("info", $"web search: done{elapsed}");
        }
    }

    private void ReleaseSessionTools()
    {
        var set = _sessionTools;
        _sessionTools = null;
        _pendingTool = null;
        _approvedTools.Clear();
        _hostedStarted.Clear();
        if (set is not null) _ = Task.Run(async () =>
        {
            try { await set.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { LogMessage("warn", $"tools: dispose failed ({ex.GetType().Name})"); }
        });
    }

    private sealed record PendingToolConfirmation(string Key, ITool Tool, JsonElement Arguments, ILiveSession Session, long Generation,
        EditIntent? Intent = null);
    private sealed record ApprovedToolCall(long At, ToolResult? Result);
    private sealed record ApprovedToolCompleted(PendingToolConfirmation Pending, ToolResult Result, long StartedAt) : PresenterEvent;
    private sealed record HostedActivityReceived(ILiveSession Session, string DelegationId, string Type, string Status) : PresenterEvent;

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
            ObserveBackground(EndAsync(EndReasons.Completed), "wrap-up end");
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
        if (_state != PresenterState.Presenting || _heardOutput || _presentation is null || NarrationHeld)
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

    private async Task OnWrapUpFallbackAsync()
    {
        if (_questionHoldOpen)
        {
            return;
        }

        if (_state == PresenterState.Presenting && _wrappingUp && !_heardOutput)
        {
            LogMessage("warn", "no wrap-up audio; ending session");
            await EndAsyncCore(false, EndReasons.Completed).ConfigureAwait(false);
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
        CancelToolConfirmation();
        _approvedTools.Clear();
        _runGeneration++;
        if (sourceCallId is not null) _navigatingCallIds[sourceCallId] = _runGeneration;
    }

    private bool PauseCore()
    {
        if (_state != PresenterState.Presenting)
        {
            return false;
        }

        ClearTimers();
        CancelToolConfirmation();
        SetInteraction(Interaction.None);
        ClosePermit();
        _session?.AppendInstructions(PromptBuilder.PauseInstruction(), $"pause-{_slideIndex + 1}");
        SetState(PresenterState.Paused);
        _guard?.Pause();
        LimitWarning?.Invoke(new PresenterLimitWarning(EndReasons.Idle, null));
        Flush?.Invoke();
        return true;
    }

    private async Task SuspendUpstreamAsync()
    {
        if (_state != PresenterState.Paused || _session is null) return;
        var session = _session;
        _session = null;
        _sessionInfo = null;
        _suspended = true;
        LiveCloseResult? result = null;
        Exception? closeError = null;
        try { result = await session.CloseAsync().ConfigureAwait(false); }
        catch (Exception ex) { closeError = ex; }
        finally { await DisposeSessionAsync(session, "pause suspension").ConfigureAwait(false); }
        AccumulateSegment(result?.Seconds);
        if (closeError is not null) LogMessage("error", $"pause close failed: {closeError.Message}");
        LogMessage("info", $"pause: upstream closed after {_settings.PauseGraceSeconds} s paused");
        UpstreamStatus?.Invoke(new PresenterUpstreamStatus("suspended"));
        PublishSnapshot();
    }

    private void AccumulateSegment(double? seconds)
    {
        _usageConfirmed &= seconds.HasValue;
        var estimated = _connectedAt is { } connected
            ? Math.Max(0, (_timeProvider.GetUtcNow() - connected).TotalSeconds) : 0;
        _priorEstimatedSeconds += estimated;
        _priorUsageSeconds += seconds ?? estimated;
        _connectedAt = null;
        _usageSeconds = _priorUsageSeconds;
        Usage?.Invoke(new PresenterUsage(_usageSeconds, _usageRatio));
    }

    private async Task<bool> ReconnectAsync()
    {
        if (!_suspended || _state != PresenterState.Paused) return false;
        UpstreamStatus?.Invoke(new PresenterUpstreamStatus("reconnecting"));
        var connection = await ConnectUpstreamAsync().ConfigureAwait(false);
        if (connection.Cancelled)
        {
            await EndAsyncCore(false, _talk?.EndReason ?? EndReasons.MaxLength).ConfigureAwait(false);
            return false;
        }
        if (connection.Session is null || connection.Info is null)
        {
            await EndAsyncCore(false, EndReasons.ReconnectFailed).ConfigureAwait(false);
            return false;
        }
        _session = connection.Session;
        _sessionInfo = connection.Info;
        _connectedAt = _timeProvider.GetUtcNow();
        _suspended = false;
        if (_muted) _session.Mute();
        OnTrainingSessionReady(reconnected: true);
        LogMessage("info", $"resume: reconnected via {connection.Label}");
        UpstreamStatus?.Invoke(new PresenterUpstreamStatus("live"));
        PublishSnapshot();
        return true;
    }

    private async Task<bool> ResumeAfterReconnectAsync()
    {
        var reconnected = _suspended;
        if (reconnected && !await ReconnectAsync().ConfigureAwait(false)) return false;
        // A held slide is replayed by the reconcile that settles its edit; a due replay is performed by ResumeCore.
        var presentedByTraining = NarrationHeld || _replayOnResume;
        var resumed = ResumeCore();
        if (resumed && reconnected && !presentedByTraining) PresentSlide(_slideIndex, interrupt: false);
        return resumed;
    }

    private async Task<bool> NavigateAfterReconnectAsync(Func<bool> navigate)
    {
        if (_suspended && !await ReconnectAsync().ConfigureAwait(false)) return false;
        return navigate();
    }

    private bool ResumeCore(bool beganDuringWaiting = false)
    {
        if (_state == PresenterState.Presenting && _interaction != Interaction.WaitingOnSlide && !beganDuringWaiting)
        {
            ClearQuestionHold();
            SetInteraction(Interaction.None);
            return false;
        }
        if (_state is not (PresenterState.Presenting or PresenterState.Paused) || _presentation is null)
        {
            return false;
        }

        var slide = _presentation.Slides[_slideIndex];
        _heardOutput = false;
        _nudgeCount = 0;
        if (!_slideDiagnosticsActive)
        {
            // A stall pause closed this slide's counts; the resumed part of the slide gets its own line.
            StartSlideDiagnostics();
        }

        CancelToolConfirmation();
        SetInteraction(Interaction.None);
        ClosePermit();
        ClearQuestionHold();
        SetState(PresenterState.Presenting);
        _guard?.StartPresenting();
        if (TrainingOverridesResume())
        {
            return true;
        }

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
        if (_state is PresenterState.Presenting or PresenterState.Paused)
        {
            _session?.Mute();
        }

        PublishSnapshot();
        return true;
    }

    private bool UnmuteCore()
    {
        _muted = false;
        if (_state is PresenterState.Presenting or PresenterState.Paused)
        {
            _session?.Unmute();
        }

        PublishSnapshot();
        return true;
    }

    private bool SendAudioCore(byte[] pcm16) =>
        (_state is PresenterState.Presenting or PresenterState.Paused) && !_muted && (_session?.SendAudio(pcm16) ?? false);

    private async Task<bool> EndAsyncCore(bool resumable, string endReason)
    {
        if (_state is PresenterState.Idle or PresenterState.Ending)
        {
            return false;
        }

        endReason = _talk?.EndReason ?? endReason;
        Debug.Assert(EndReasons.All.Contains(endReason));
        _requestedEndReason = endReason;
        _talkEndedAt = endReason == EndReasons.MaxLength ? _guard?.MaxEndsAt : _timeProvider.GetUtcNow();
        StopGuard();
        CancelRun();

        CompleteSlideDiagnostics();
        ClearTimers();
        ResetUtterance();
        SetInteraction(Interaction.None);
        ClosePermit();
        _navigatingCallIds.Clear();
        _voicedIntervals.Clear();
        _lastVoicedAt = 0;
        _runGeneration++;
        _toolRoundTracker.Clear();
        ReleaseSessionTools();
        _endResumable = resumable;
        var session = _session;
        SetState(PresenterState.Ending);
        Flush?.Invoke();
        await CloseTrainingTalkAsync().ConfigureAwait(false);
        if (session is null)
        {
            var reason = endReason switch
            {
                EndReasons.ReconnectFailed => _endDiagnostic ?? "connect_failed",
                EndReasons.Shutdown => "disposed",
                _ => _suspended ? "suspended" : "close_requested"
            };
            OnClosed(reason, null, endReason);
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
            OnClosed("connection_lost", null, endReason);
            await DisposeSessionAsync(session, "failed close").ConfigureAwait(false);
            throw;
        }
    }

    private async Task OnSessionClosedAsync(ILiveSession session, string reason, double? seconds)
    {
        try { OnClosed(reason, seconds, _requestedEndReason ?? EndReasons.UpstreamLost); }
        finally { await DisposeSessionAsync(session, "session close").ConfigureAwait(false); }
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

    private void OnClosed(string reason, double? seconds, string endReason)
    {
        CancelRun();
        Debug.Assert(EndReasons.All.Contains(endReason));
        _talkEndedAt ??= _timeProvider.GetUtcNow();
        StopGuard();
        CompleteSlideDiagnostics();
        ClearTimers();
        ResetUtterance();
        SetInteraction(Interaction.None);
        ClosePermit();
        _navigatingCallIds.Clear();
        _voicedIntervals.Clear();
        _lastVoicedAt = 0;
        _runGeneration++;
        _toolRoundTracker.Clear();
        ReleaseSessionTools();
        ResetTrainingOnClosed();
        var endedNormally = IsNormalClose(reason) && !_endResumable;
        if (_presentation is not null)
        {
            _lastRun = new LastRun(_presentation.Id, _slideIndex, endedNormally);
        }

        var currentEstimate = _connectedAt is { } connected
            ? Math.Max(0, (_timeProvider.GetUtcNow() - connected).TotalSeconds) : 0;
        var estimate = _priorEstimatedSeconds + currentEstimate;
        if (_connectedAt is not null) _usageConfirmed &= seconds.HasValue;
        _usageSeconds = _priorUsageSeconds + (seconds ?? currentEstimate);
        double? confirmedSeconds = _usageConfirmed ? _usageSeconds : null;

        _session = null;
        _sessionInfo = null;
        LogMessage(
            "info",
            $"closed: reason={reason} end={endReason} usage={(confirmedSeconds?.ToString() ?? "unconfirmed")} s" +
            $" (estimated {estimate:0} s){(endedNormally ? string.Empty : $" (Start resumes at slide {_slideIndex + 1})")}");
        Closed?.Invoke(new PresenterClosed(reason, confirmedSeconds, endReason, _usageConfirmed, estimate,
            _talkStartedAt, _talkEndedAt));
        _suspended = false;
        _connectedAt = null;
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
        if (!_questionHoldOpen || _interaction is Interaction.Answering or Interaction.AwaitingCarryOn or Interaction.WaitingOnSlide or Interaction.AwaitingConfirmQuestion or Interaction.AwaitingConfirmAnswer)
        {
            return;
        }

        ResumeAfterQuestion("question: released after 15 s without an answer");
    }

    // An open hold never lets a timer advance. Once the answer has been followed by FollowUpWaitMs of quiet, the
    // model is told to resume the slide; the ordinary timers take over from there.
    private bool HoldBlocksProgress()
    {
        if (_pendingTool is not null || NarrationHeld) return true;
        if (!_questionHoldOpen)
        {
            return false;
        }

        if (_answerVoiced && _interaction == Interaction.None)
        {
            ResumeAfterQuestion($"question: no follow-up after {FollowUpWaitMs} ms; resuming");
        }

        return true;
    }

    private void ResumeAfterQuestion(string message)
    {
        LogMessage("info", message);
        ClearQuestionHold();
        if (NarrationHeld) return;
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
        // Late audio of a held slide must not schedule its next part or an advance.
        if (NarrationHeld) return;
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
        _rangeReply = false;
        _answerVoiced = false;
        _latestQuestionEndMs = null;
        ClearQuestionTimer();
        if (_interaction is Interaction.Answering or Interaction.AwaitingCarryOn or Interaction.WaitingOnSlide)
        {
            SetInteraction(Interaction.None);
        }
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
        SetInteraction(Interaction.None);
        ClearQuestionHold();
        ClosePermit();
        if (_state != PresenterState.Paused)
        {
            return;
        }

        SetState(PresenterState.Presenting);
        _guard?.StartPresenting();
    }

    private bool PartsPending => _partsSent < _parts.Count;

    private void RecordActivity()
    {
        if (_state == PresenterState.Presenting && _guard?.Activity() == true)
            LimitWarning?.Invoke(new PresenterLimitWarning(EndReasons.Idle, null));
    }

    private void CancelRun()
    {
        try { _runCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void StopGuard()
    {
        Interlocked.Exchange(ref _guard, null)?.Dispose();
    }

    private async Task OnGuardElapsedAsync()
    {
        if (_guard is null || _state is PresenterState.Idle or PresenterState.Ending) return;
        var due = _guard.Due();
        if (due.Max)
        {
            await EndAsyncCore(false, EndReasons.MaxLength).ConfigureAwait(false);
            return;
        }
        if (due.Idle)
        {
            await EndAsyncCore(false, EndReasons.Idle).ConfigureAwait(false);
            return;
        }
        if (due.MaxWarning) RaiseLimitWarning(EndReasons.MaxLength);
        if (due.IdleWarning) RaiseLimitWarning(EndReasons.Idle);
        if (due.Pause) await SuspendUpstreamAsync().ConfigureAwait(false);
    }

    private void RaiseLimitWarning(string kind)
    {
        LogMessage("warn", $"limit: {kind} warning, {TalkGuard.WarningLead} s left");
        LimitWarning?.Invoke(new PresenterLimitWarning(kind, TalkGuard.WarningLead));
        if (_state == PresenterState.Presenting && _session is not null)
            _session.AppendInstructions(PromptBuilder.LimitWarningInstruction(kind), $"limit-{kind}-warning");
    }

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
        AdvanceSilenceMs,
        _suspended);

    private void LogMessage(string level, string message) =>
        Log?.Invoke(new PresenterLog(level, UntrustedLogText.Sanitize(message, 1024)));

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

    private sealed record StartCommand(string OwnerId, string Id, int? FromIndex, int? MaxMinutes,
        StartTicket Ticket) : PresenterEvent
    {
        public TaskCompletionSource<PresenterStartResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// One Start's cancellation handle, created when StartAsync enqueues it. Once accepted it is the talk's handle
    /// (load, connects, reconnects) until the next accepted Start. The end reason is first-wins.
    /// </summary>
    private sealed class StartTicket
    {
        private readonly CancellationTokenSource _cts = new();
        private string? _endReason;

        public CancellationToken Token => _cts.Token;
        public string? EndReason => Volatile.Read(ref _endReason);

        public void RecordEndReason(string? endReason)
        {
            if (endReason is not null) Interlocked.CompareExchange(ref _endReason, endReason, null);
        }

        public void Cancel(string? endReason = null)
        {
            RecordEndReason(endReason);
            _cts.Cancel();
        }
    }
    private sealed record NextCommand : Command;
    private sealed record PrevCommand : Command;
    private sealed record GotoCommand(int Index) : Command;
    private sealed record PauseCommand : Command;
    private sealed record ConfirmEndCommand(bool Confirmed) : Command;
    private sealed record ResumeCommand : Command;
    private sealed record MuteCommand : Command;
    private sealed record UnmuteCommand : Command;
    private sealed record SendAudioCommand(byte[] Pcm16) : Command;
    private sealed record EndCommand(bool Resumable, string EndReason) : Command;
    private sealed record GuardElapsed(TalkGuard Source, long Generation) : PresenterEvent;
    private sealed record AudioReceived(ILiveSession Session, byte[] Bytes, long? StartMs, long? EndMs) : PresenterEvent;
    private sealed record TranscriptReceived(ILiveSession Session, string Role, string Delta, long? StartMs, long? EndMs) : PresenterEvent;
    private sealed record UsageReceived(ILiveSession Session, double Seconds, double? Ratio) : PresenterEvent;
    private sealed record AppendedReceived(ILiveSession Session, string Kind, string? ClientEventId, JsonElement Raw) : PresenterEvent;
    private sealed record DelegationReceived(ILiveSession Session, JsonElement Raw) : PresenterEvent;
    private sealed record UpstreamErrorReceived(ILiveSession Session, JsonElement Raw) : PresenterEvent;
    private sealed record DelegatedResponseReceived(ILiveSession Session, string DelegationId, string Type) : PresenterEvent;
    private sealed record ToolCallReceived(ILiveSession Session, string DelegationId, string CallId, string Name, string Arguments) : PresenterEvent;
    private sealed record ToolInvocationCompleted(ILiveSession Session, long RunGeneration, string DelegationId, string CallId, ToolResult Result,
        string? ToolSource = null, string? ToolName = null, long StartedAt = 0) : PresenterEvent;
    private sealed record SessionWarning(ILiveSession Session, string Message) : PresenterEvent;
    private sealed record SessionClosed(ILiveSession Session, string Reason, double? Seconds) : PresenterEvent;
    private sealed record SilenceElapsed(long Generation, bool PartGap) : PresenterEvent;
    private sealed record NudgeElapsed(long Generation) : PresenterEvent;
    private sealed record WrapUpFallbackElapsed(long Generation) : PresenterEvent;
    private sealed record QuestionHoldElapsed(long Generation) : PresenterEvent;
    private sealed record UtteranceElapsed(long Generation) : PresenterEvent;
    private sealed record InteractionElapsed(long Generation) : PresenterEvent;
    private sealed record PermitElapsed(long Generation) : PresenterEvent;
    private sealed record BackgroundFailure(string Operation, string Message) : PresenterEvent;
    private sealed record Barrier(TaskCompletionSource Completion) : PresenterEvent;
    private sealed record Shutdown(TaskCompletionSource Completion) : PresenterEvent;
    private sealed record LastRun(string Id, int Index, bool EndedNormally);
}
