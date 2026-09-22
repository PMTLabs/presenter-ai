using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Sessions;
using PresenterAi.Infrastructure.Persistence;
using PresenterAi.Infrastructure.Persistence.Entities;

namespace PresenterAi.Infrastructure.Sessions;

/// <summary>
/// Serialises one presenter's events on a private worker. The presenter loop only performs a non-blocking
/// channel write; every database operation gets its own scoped DbContext.
/// </summary>
public sealed class SessionRecorder : ISessionRecorder
{
    private static readonly TimeSpan FinalFlushBound = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SessionRecorder> _logger;
    private readonly Channel<Work> _work = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _endCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _beginCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<EventWork> _buffer = [];
    private readonly Task _worker;
    private readonly object _handlerLock = new();
    private readonly object _requestLock = new();

    private IPresenter? _presenter;
    private Action<int>? _slideHandler;
    private Action<PresenterTranscript>? _transcriptHandler;
    private Action<PresenterUsage>? _usageHandler;
    private Action<PresenterClosed>? _closedHandler;
    private int _beginRequested;
    private int _begun;
    private int _endRequested;
    private int _disposed;
    private bool _aborted;
    // Only the single worker reads this flag. The first end/closed work item wins, so no later event can
    // overwrite the row after the barrier has completed.
    private bool _finalized;
    private string? _sessionId;
    private int _ordinal;
    private int? _currentSlideNo;
    private TurnBuffer? _turn;
    private double _lastUsageSeconds;
    private bool _hasUsage;

    public SessionRecorder(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<SessionRecorder> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _worker = Task.Run(ProcessWorkAsync);
    }

    public void Attach(IPresenter presenter)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        lock (_handlerLock)
        {
            if (_presenter is not null)
            {
                throw new InvalidOperationException("The session recorder is already attached.");
            }

            _presenter = presenter;
            _slideHandler = OnSlide;
            _transcriptHandler = OnTranscript;
            _usageHandler = OnUsage;
            _closedHandler = OnClosed;
            presenter.Slide += _slideHandler;
            presenter.Transcript += _transcriptHandler;
            presenter.Usage += _usageHandler;
            presenter.Closed += _closedHandler;
        }
    }

    public void Detach()
    {
        lock (_handlerLock)
        {
            if (_presenter is null)
            {
                return;
            }

            _presenter.Slide -= _slideHandler;
            _presenter.Transcript -= _transcriptHandler;
            _presenter.Usage -= _usageHandler;
            _presenter.Closed -= _closedHandler;
            _presenter = null;
            _slideHandler = null;
            _transcriptHandler = null;
            _usageHandler = null;
            _closedHandler = null;
        }
    }

    public async Task BeginAsync(string userId, PresenterStartResult result, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(result);

        var requested = false;
        lock (_requestLock)
        {
            if (Volatile.Read(ref _beginRequested) == 0 && Volatile.Read(ref _endRequested) == 0 && Volatile.Read(ref _disposed) == 0)
            {
                Volatile.Write(ref _beginRequested, 1);
                requested = true;
                if (!_work.Writer.TryWrite(new BeginWork(userId, result)))
                {
                    _beginCompletion.TrySetResult();
                }
            }
        }

        if (!requested && Volatile.Read(ref _endRequested) != 0)
        {
            _beginCompletion.TrySetResult();
        }

        await _beginCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task EndAsync(string closeReason = "disconnect", double? seconds = null)
    {
        lock (_requestLock)
        {
            if (Volatile.Read(ref _endRequested) != 0)
            {
                return _endCompletion.Task;
            }

            Volatile.Write(ref _endRequested, 1);
            if (Volatile.Read(ref _beginRequested) == 0 || Volatile.Read(ref _disposed) != 0)
            {
                // No begin was requested, so this recorder represents a failed start and has no row to flush.
                _aborted = true;
                _endCompletion.TrySetResult();
                return _endCompletion.Task;
            }

            if (!_work.Writer.TryWrite(new EndWork(
                    string.IsNullOrWhiteSpace(closeReason) ? "disconnect" : closeReason,
                    seconds)))
            {
                _endCompletion.TrySetResult();
                return _endCompletion.Task;
            }
        }

        _ = EnforceFinalFlushBoundAsync();
        return _endCompletion.Task;
    }

    private void OnSlide(int index) => Enqueue(new SlideWork(index + 1));

    private void OnTranscript(PresenterTranscript transcript) =>
        Enqueue(new TranscriptWork(
            transcript.Role,
            transcript.Delta,
            _timeProvider.GetUtcNow()));

    private void OnUsage(PresenterUsage usage) => Enqueue(new UsageWork(usage.Seconds));

    private void OnClosed(PresenterClosed closed) =>
        Enqueue(new ClosedWork(closed.Reason, closed.Seconds));

    private void Enqueue(EventWork work)
    {
        try
        {
            if (!_work.Writer.TryWrite(work))
            {
                _logger.LogWarning("Session recorder queue rejected an event");
            }
        }
        catch (Exception exception)
        {
            // Presenter event handlers are on the presenter loop. Recording must never affect that loop.
            _logger.LogWarning(exception, "Session recorder could not queue an event");
        }
    }

    private async Task ProcessWorkAsync()
    {
        try
        {
            await foreach (var work in _work.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    switch (work)
                    {
                        case EventWork eventWork when Volatile.Read(ref _begun) == 0:
                            if (!_aborted)
                            {
                                _buffer.Add(eventWork);
                            }

                            break;
                        case BeginWork begin:
                            await ProcessBeginAsync(begin).ConfigureAwait(false);
                            break;
                        case EventWork eventWork:
                            await ProcessEventAsync(eventWork).ConfigureAwait(false);
                            break;
                        case EndWork end:
                            await ProcessEndAsync(end).ConfigureAwait(false);
                            break;
                    }
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    // A bad database operation must not kill the barrier worker or presenter.
                    _logger.LogError(exception, "Session recording operation failed");
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _beginCompletion.TrySetResult();
        }
    }

    private async Task ProcessBeginAsync(BeginWork begin)
    {
        // BeginWork and EndWork are FIFO. EndAsync may already have requested the end, but because it observed
        // _beginRequested it queued EndWork behind this item; creating the row here prevents a started run from
        // disappearing. The lock in the public methods handles the reverse both-flags race by making a late begin
        // a no-op when EndAsync won before begin was requested.
        if (_aborted)
        {
            _beginCompletion.TrySetResult();
            return;
        }

        Volatile.Write(ref _begun, 1);
        var session = new Session
        {
            PresentationId = begin.Result.PresentationId,
            UserId = begin.UserId,
            StartedAt = _timeProvider.GetUtcNow(),
            Upstream = begin.Result.Upstream ?? string.Empty,
            UpstreamSessionId = begin.Result.UpstreamSessionId
        };

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PresenterAiDbContext>();
            db.Sessions.Add(session);
            await db.SaveChangesAsync(_lifetime.Token).ConfigureAwait(false);
            _sessionId = session.Id;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not create recorded session for presentation {PresentationId}", begin.Result.PresentationId);
        }

        var buffered = _buffer.ToArray();
        _buffer.Clear();
        foreach (var eventWork in buffered)
        {
            await ProcessEventAsync(eventWork).ConfigureAwait(false);
        }

        _beginCompletion.TrySetResult();
    }

    private async Task ProcessEventAsync(EventWork work)
    {
        switch (work)
        {
            case SlideWork slide:
                _currentSlideNo = slide.SlideNo;
                break;
            case TranscriptWork transcript:
                await ProcessTranscriptAsync(transcript).ConfigureAwait(false);
                break;
            case UsageWork usage:
                _lastUsageSeconds = usage.Seconds;
                _hasUsage = true;
                break;
            case ClosedWork closed:
                await ProcessEndAsync(new EndWork(closed.Reason, closed.Seconds)).ConfigureAwait(false);
                break;
        }
    }

    private async Task ProcessTranscriptAsync(TranscriptWork transcript)
    {
        if (_turn is not null && !string.Equals(_turn.Role, transcript.Role, StringComparison.Ordinal))
        {
            await PersistTurnAsync(_turn).ConfigureAwait(false);
            _turn = null;
        }

        if (_turn is null)
        {
            _turn = new TurnBuffer(
                transcript.Role,
                transcript.Delta,
                _currentSlideNo,
                transcript.At);
        }
        else
        {
            _turn = _turn with { Text = _turn.Text + transcript.Delta };
        }
    }

    private async Task ProcessEndAsync(EndWork end)
    {
        // This is deliberately worker-local: ClosedWork and the bridge EndWork can both be queued for one run,
        // but only the first item may write final metadata or complete the barrier.
        if (_finalized)
        {
            return;
        }

        _finalized = true;
        if (Volatile.Read(ref _endRequested) == 0)
        {
            Volatile.Write(ref _endRequested, 1);
            _ = EnforceFinalFlushBoundAsync();
        }

        if (_sessionId is null)
        {
            _endCompletion.TrySetResult();
            return;
        }

        var turn = _turn;
        _turn = null;
        var endedAt = _timeProvider.GetUtcNow();
        var usageSeconds = end.Seconds ?? (_hasUsage ? _lastUsageSeconds : 0d);

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PresenterAiDbContext>();
            var session = await db.Sessions.SingleOrDefaultAsync(
                candidate => candidate.Id == _sessionId,
                _lifetime.Token).ConfigureAwait(false);
            if (session is not null)
            {
                if (turn is not null)
                {
                    db.SessionTurns.Add(CreateTurn(turn));
                }

                session.EndedAt = endedAt;
                session.UsageSeconds = ToRoundedSeconds(usageSeconds);
                session.CloseReason = string.IsNullOrWhiteSpace(end.CloseReason) ? "disconnect" : end.CloseReason;
                await db.SaveChangesAsync(_lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not finalise recorded session {SessionId}", _sessionId);
        }
        finally
        {
            _endCompletion.TrySetResult();
        }
    }

    private async Task PersistTurnAsync(TurnBuffer turn)
    {
        if (_sessionId is null)
        {
            return;
        }

        var ordinal = _ordinal++;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PresenterAiDbContext>();
            db.SessionTurns.Add(new SessionTurn
            {
                SessionId = _sessionId,
                Ordinal = ordinal,
                Role = turn.Role,
                Text = turn.Text,
                SlideNo = turn.SlideNo,
                At = turn.At
            });
            await db.SaveChangesAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not persist a turn for recorded session {SessionId}", _sessionId);
        }
    }

    private SessionTurn CreateTurn(TurnBuffer turn) => new()
    {
        SessionId = _sessionId!,
        Ordinal = _ordinal++,
        Role = turn.Role,
        Text = turn.Text,
        SlideNo = turn.SlideNo,
        At = turn.At
    };

    private async Task EnforceFinalFlushBoundAsync()
    {
        try
        {
            await Task.Delay(FinalFlushBound).ConfigureAwait(false);
            if (!_endCompletion.Task.IsCompleted)
            {
                _logger.LogError("Session recorder final flush exceeded {BoundSeconds} seconds", FinalFlushBound.TotalSeconds);
                _lifetime.Cancel();
                _endCompletion.TrySetResult();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static int ToRoundedSeconds(double seconds) =>
        Convert.ToInt32(Math.Round(Math.Max(0, seconds), MidpointRounding.AwayFromZero));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Detach();
        _work.Writer.TryComplete();
        _lifetime.Cancel();
        try
        {
            await _worker.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Session recorder worker did not stop during disposal");
        }

        _lifetime.Dispose();
        _beginCompletion.TrySetResult();
        _endCompletion.TrySetResult();
    }

    private abstract record Work;
    private abstract record EventWork : Work;
    private sealed record SlideWork(int SlideNo) : EventWork;
    private sealed record TranscriptWork(string Role, string Delta, DateTimeOffset At) : EventWork;
    private sealed record UsageWork(double Seconds) : EventWork;
    private sealed record ClosedWork(string Reason, double? Seconds) : EventWork;
    private sealed record BeginWork(string UserId, PresenterStartResult Result) : Work;
    private sealed record EndWork(string CloseReason, double? Seconds) : Work;
    private sealed record TurnBuffer(string Role, string Text, int? SlideNo, DateTimeOffset At);
}

public sealed class SessionRecorderFactory(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory) : ISessionRecorderFactory
{
    public ISessionRecorder Create() =>
        new SessionRecorder(scopeFactory, timeProvider, loggerFactory.CreateLogger<SessionRecorder>());
}
