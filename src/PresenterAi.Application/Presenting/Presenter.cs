using System.Text.Json;
using System.Threading.Channels;
using PresenterAi.Application.Scripts;

namespace PresenterAi.Application.Presenting;

public sealed record SessionRequest(string Instructions, string Voice);

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
    public const int MaxUpstreamAttempts = 4;
    public const int PartGapMs = 2_500;

    private readonly Func<SessionRequest, int, ILiveSession?> _createSession;
    private readonly Func<string, CancellationToken, Task<LoadedPresentation>> _loadPresentation;
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
    private bool _nudged;
    private bool _wrappingUp;
    private IReadOnlyList<string> _parts = Array.Empty<string>();
    private int _partsSent;
    private double _usageSeconds;
    private double? _usageRatio;
    private LastRun? _lastRun;
    private ITimer? _silenceTimer;
    private ITimer? _nudgeTimer;
    private ITimer? _wrapUpTimer;
    private long _silenceGeneration;
    private long _nudgeGeneration;
    private long _wrapUpGeneration;
    private PresenterSnapshot _snapshot;
    private int _disposed;

    public Presenter(
        Func<SessionRequest, int, ILiveSession?> createSession,
        Func<string, CancellationToken, Task<LoadedPresentation>> loadPresentation,
        PresenterSettings? settings = null,
        TimeProvider? timeProvider = null)
    {
        _createSession = createSession ?? throw new ArgumentNullException(nameof(createSession));
        _loadPresentation = loadPresentation ?? throw new ArgumentNullException(nameof(loadPresentation));
        _settings = settings ?? new PresenterSettings();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _snapshot = BuildSnapshot();
        _loop = Task.Run(RunLoopAsync);
    }

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

    public Task<bool> StartAsync(string id, int? fromIndex = null, CancellationToken cancellationToken = default) =>
        EnqueueCommandAsync(new StartCommand(id, fromIndex), cancellationToken);

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

    public Task<bool> EndAsync(CancellationToken cancellationToken = default) =>
        EnqueueCommandAsync(new EndCommand(), cancellationToken);

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
            _ = QueueFromProducerAsync(presenterEvent);
        }
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
                            OnClosed(closed.Reason, closed.Seconds);
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
                    case Barrier barrier:
                        barrier.Completion.TrySetResult();
                        break;
                    case Shutdown shutdown:
                        ClearTimers();
                        _session?.Terminate();
                        shutdown.Completion.TrySetResult();
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessCommandAsync(Command command)
    {
        try
        {
            var result = command switch
            {
                StartCommand start => await StartAsyncCore(start.Id, start.FromIndex).ConfigureAwait(false),
                NextCommand => NextCore(),
                PrevCommand => PrevCore(),
                GotoCommand goTo => GotoCore(goTo.Index),
                PauseCommand => PauseCore(),
                ResumeCommand => ResumeCore(),
                MuteCommand => MuteCore(),
                UnmuteCommand => UnmuteCore(),
                SendAudioCommand audio => SendAudioCore(audio.Pcm16),
                EndCommand => await EndAsyncCore().ConfigureAwait(false),
                _ => false
            };
            command.Completion.TrySetResult(result);
        }
        catch (Exception exception)
        {
            command.Completion.TrySetException(exception);
        }
    }

    private async Task<bool> StartAsyncCore(string id, int? fromIndex)
    {
        if (_state != PresenterState.Idle)
        {
            LogMessage("warn", $"start ignored: state is {StateName(_state)}");
            return false;
        }

        SetState(PresenterState.Connecting);
        try
        {
            _presentation = await _loadPresentation(id, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogMessage("error", $"cannot load presentation \"{id}\": {exception.Message}");
            UpstreamError?.Invoke(new PresenterUpstreamError($"cannot load presentation: {exception.Message}", "presentation"));
            SetState(PresenterState.Idle);
            return false;
        }

        var presentation = _presentation;
        var instructions = PromptBuilder.SystemInstructions(
            presentation.Meta.Title,
            presentation.Slides,
            presentation.Context,
            onWarn: message => LogMessage("warn", message));
        ILiveSession? session = null;
        LiveSessionInfo? sessionInfo = null;
        for (var attempt = 0; attempt < MaxUpstreamAttempts; attempt++)
        {
            var candidate = _createSession(new SessionRequest(instructions, presentation.Meta.Voice ?? _settings.Voice), attempt);
            if (candidate is null)
            {
                break;
            }

            var label = candidate.Name ?? $"upstream #{attempt + 1}";
            try
            {
                sessionInfo = await candidate.ConnectAsync(_lifetime.Token).ConfigureAwait(false);
                session = candidate;
                if (attempt > 0)
                {
                    LogMessage("warn", $"connected via {label}");
                }

                break;
            }
            catch (Exception exception)
            {
                LogMessage("error", $"session start via {label} failed: {exception.Message}");
                var startup = exception as LiveStartupException;
                UpstreamError?.Invoke(new PresenterUpstreamError($"{label}: {exception.Message}", startup?.Code ?? "connect"));
            }
        }

        if (session is null || sessionInfo is null)
        {
            LogMessage("error", "no upstream could start a session");
            SetState(PresenterState.Idle);
            return false;
        }

        _session = session;
        _sessionInfo = sessionInfo;
        WireSession(session);
        var startAt = fromIndex
            ?? (_lastRun is { EndedNormally: false } last && last.Id == id ? last.Index : 0);
        startAt = Math.Clamp(startAt, 0, Math.Max(0, presentation.Slides.Count - 1));
        _muted = false;
        _usageSeconds = 0;
        _usageRatio = null;
        SetState(PresenterState.Presenting);
        PresentSlide(startAt, interrupt: false);
        return true;
    }

    private void WireSession(ILiveSession session)
    {
        session.Audio += (bytes, startMs, endMs) => QueueFromProducer(new AudioReceived(session, bytes.ToArray(), startMs, endMs));
        session.Transcript += (role, delta, startMs, endMs) => QueueFromProducer(new TranscriptReceived(session, role, delta, startMs, endMs));
        session.Appended += (kind, clientEventId, raw) => QueueFromProducer(new AppendedReceived(session, kind, clientEventId, raw.Clone()));
        session.Usage += (seconds, ratio) => QueueFromProducer(new UsageReceived(session, seconds, ratio));
        session.Delegation += raw => QueueFromProducer(new DelegationReceived(session, raw.Clone()));
        session.UpstreamError += raw => QueueFromProducer(new UpstreamErrorReceived(session, raw.Clone()));
        session.Closed += (reason, seconds) => QueueFromProducer(new SessionClosed(session, reason, seconds));
    }

    private void PresentSlide(int index, bool interrupt)
    {
        if (_presentation is null || index < 0 || index >= _presentation.Slides.Count)
        {
            return;
        }

        ClearTimers();
        _slideIndex = index;
        _heardOutput = false;
        _nudged = false;
        _wrappingUp = false;
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
        if (_state is not (PresenterState.Presenting or PresenterState.Paused) || !AudioLevel.IsVoiced(audio.Bytes))
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
            ArmAfterVoice();
        }
    }

    private void OnTranscript(TranscriptReceived transcript)
    {
        if (!ReferenceEquals(transcript.Session, _session))
        {
            return;
        }

        Transcript?.Invoke(new PresenterTranscript(transcript.Role, transcript.Delta, transcript.StartMs, transcript.EndMs));
        if (transcript.Role == "user" && _state == PresenterState.Presenting && _heardOutput)
        {
            ArmAfterVoice();
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

        LogMessage("info", $"delegation created ({JsonString(delegation.Raw, "target")}) id={JsonString(delegation.Raw, "id")} — ignored (client delegation carries no task text)");
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
    }

    private void OnPartGap()
    {
        if (_state == PresenterState.Presenting && PartsPending)
        {
            SendNextPart();
        }
    }

    private void OnSilence()
    {
        if (_state != PresenterState.Presenting || !_heardOutput || PartsPending)
        {
            return;
        }

        if (_wrappingUp)
        {
            LogMessage("info", "wrap-up finished; ending session");
            _ = EndAsyncCore();
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
        if (_state != PresenterState.Presenting || _heardOutput || _nudged || _presentation is null)
        {
            return;
        }

        _nudged = true;
        var slide = _presentation.Slides[_slideIndex];
        LogMessage("warn", $"no output audio {NudgeMs} ms after slide {_slideIndex + 1} was sent; nudging the model");
        _session?.AppendInstructions(
            PromptBuilder.NudgeInstruction(_slideIndex, SlideCount, slide.Title),
            $"slide-{_slideIndex + 1}-nudge");
    }

    private void OnWrapUpFallback()
    {
        if (_state == PresenterState.Presenting && _wrappingUp && !_heardOutput)
        {
            LogMessage("warn", "no wrap-up audio; ending session");
            _ = EndAsyncCore();
        }
    }

    private void StartWrapUp()
    {
        if (_wrappingUp)
        {
            return;
        }

        ClearTimers();
        _wrappingUp = true;
        _parts = Array.Empty<string>();
        _partsSent = 0;
        _heardOutput = false;
        LogMessage("info", "last slide finished; sending wrap-up");
        _session?.AppendInstructions(PromptBuilder.WrapUpInstruction(), "wrap-up");
        ArmWrapUpFallback();
    }

    private bool NextCore()
    {
        if (!CanNavigate())
        {
            return false;
        }

        LeavePauseForNavigation();
        if (_slideIndex >= SlideCount - 1)
        {
            StartWrapUp();
            return true;
        }

        LogMessage("info", $"manual next → slide {_slideIndex + 2}");
        PresentSlide(_slideIndex + 1, interrupt: true);
        return true;
    }

    private bool PrevCore()
    {
        if (!CanNavigate())
        {
            return false;
        }

        LeavePauseForNavigation();
        var target = Math.Max(0, _slideIndex - 1);
        LogMessage("info", $"manual prev → slide {target + 1}");
        PresentSlide(target, interrupt: true);
        return true;
    }

    private bool GotoCore(int index)
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
        PresentSlide(index, interrupt: true);
        return true;
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
        _nudged = false;
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

    private async Task<bool> EndAsyncCore()
    {
        if (_state is PresenterState.Idle or PresenterState.Ending)
        {
            return false;
        }

        ClearTimers();
        var session = _session;
        SetState(PresenterState.Ending);
        if (session is null)
        {
            OnClosed("close_requested", null);
            return true;
        }

        await session.CloseAsync().ConfigureAwait(false);
        return true;
    }

    private void OnClosed(string reason, double? seconds)
    {
        ClearTimers();
        var endedNormally = IsNormalClose(reason);
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
    }

    private sealed record StartCommand(string Id, int? FromIndex) : Command;
    private sealed record NextCommand : Command;
    private sealed record PrevCommand : Command;
    private sealed record GotoCommand(int Index) : Command;
    private sealed record PauseCommand : Command;
    private sealed record ResumeCommand : Command;
    private sealed record MuteCommand : Command;
    private sealed record UnmuteCommand : Command;
    private sealed record SendAudioCommand(byte[] Pcm16) : Command;
    private sealed record EndCommand : Command;
    private sealed record AudioReceived(ILiveSession Session, byte[] Bytes, long? StartMs, long? EndMs) : PresenterEvent;
    private sealed record TranscriptReceived(ILiveSession Session, string Role, string Delta, long? StartMs, long? EndMs) : PresenterEvent;
    private sealed record UsageReceived(ILiveSession Session, double Seconds, double? Ratio) : PresenterEvent;
    private sealed record AppendedReceived(ILiveSession Session, string Kind, string? ClientEventId, JsonElement Raw) : PresenterEvent;
    private sealed record DelegationReceived(ILiveSession Session, JsonElement Raw) : PresenterEvent;
    private sealed record UpstreamErrorReceived(ILiveSession Session, JsonElement Raw) : PresenterEvent;
    private sealed record SessionClosed(ILiveSession Session, string Reason, double? Seconds) : PresenterEvent;
    private sealed record SilenceElapsed(long Generation, bool PartGap) : PresenterEvent;
    private sealed record NudgeElapsed(long Generation) : PresenterEvent;
    private sealed record WrapUpFallbackElapsed(long Generation) : PresenterEvent;
    private sealed record Barrier(TaskCompletionSource Completion) : PresenterEvent;
    private sealed record Shutdown(TaskCompletionSource Completion) : PresenterEvent;
    private sealed record LastRun(string Id, int Index, bool EndedNormally);
}
