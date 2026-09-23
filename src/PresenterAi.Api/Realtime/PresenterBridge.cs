using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PresenterAi.Api.Errors;
using Microsoft.Extensions.Options;
using PresenterAi.Application.Auth;
using PresenterAi.Application.Content;
using PresenterAi.Contracts;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Sessions;
using PresenterAi.Infrastructure.Redis;

namespace PresenterAi.Api.Realtime;

/// <summary>Owns the process-wide browser slot and routes presenter events to its current connection.</summary>
public sealed class PresenterBridge : IAsyncDisposable
{
    private const string BusyMessage = "Another presenter page is already connected. Close it first.";
    // LiveSession.CloseAsync is itself bounded, so only a wedged presenter loop reaches this.
    private static readonly TimeSpan EndToIdleBound = TimeSpan.FromSeconds(5);
    // There are at most two routes (primary and an optional fallback), each with two 10-second handshakes
    // (LiveSessionOptions.HandshakeTimeout). Repository load is bounded only by Npgsql's retry strategy.
    // Beyond this 90-second observation bound cleanup proceeds; a run lost in that window is best-effort
    // recording (D8).
    private static readonly TimeSpan StartObservationBound = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ServerCloseBound = TimeSpan.FromSeconds(1);
    private TimeSpan _takeOverBound = TimeSpan.FromSeconds(15);
    private const int MaxAuthenticationFrameBytes = 4 * 1024;
    // Browser commands contain only a type, opaque presentation id and small numeric fields; 4 KiB is generous.
    private const int MaxTextCommandBytes = 4 * 1024;
    // The capture worklet sends 480 PCM16 samples (960 bytes) every 20 ms; permit four frames for transport margin.
    private const int MaxAudioFrameBytes = 4 * 1024;
    private readonly IPresenter _presenter;
    private readonly ILogger<PresenterBridge> _logger;
    private readonly ITicketStore _ticketStore;
    private readonly ISessionRecorderFactory _recorderFactory;
    private readonly TimeSpan _authFrameTimeout;
    private readonly SemaphoreSlim _pendingAuth;
    private ClientConnection? _client;
    private int _outboundCapacity = 500;
    private Func<Task>? _beforeSocketSendAsync;

    public PresenterBridge(
        IPresenter presenter,
        ILogger<PresenterBridge> logger,
        ITicketStore ticketStore,
        ISessionRecorderFactory recorderFactory,
        IOptions<SessionRedisOptions> sessionOptions)
    {
        _presenter = presenter;
        _logger = logger;
        _ticketStore = ticketStore;
        _recorderFactory = recorderFactory;
        _authFrameTimeout = TimeSpan.FromSeconds(sessionOptions.Value.AuthFrameTimeoutSeconds);
        _pendingAuth = new SemaphoreSlim(sessionOptions.Value.MaxPendingAuthConnections, sessionOptions.Value.MaxPendingAuthConnections);
        // One process-wide subscription: handlers never block the Presenter loop and only route to its owner.
        presenter.State += state => Current?.EnqueueText(StateFrame(state));
        presenter.Slide += index => Current?.EnqueueText(new { type = "slide", index });
        presenter.Audio += audio => Current?.EnqueueBinary(audio.Bytes.ToArray());
        presenter.Transcript += transcript => Current?.EnqueueText(new { type = "transcript", role = transcript.Role, delta = transcript.Delta, start_ms = transcript.StartMs, end_ms = transcript.EndMs });
        presenter.Usage += usage => Current?.EnqueueText(new { type = "usage", seconds = usage.Seconds, ratio = usage.Ratio });
        presenter.Closed += closed => Current?.EnqueueText(new { type = "closed", reason = closed.Reason, seconds = closed.Seconds });
        presenter.Log += log => Current?.EnqueueText(new { type = "log", level = log.Level, message = log.Message });
        presenter.UpstreamError += error => Current?.EnqueueText(new { type = "error", message = error.Message, code = error.Code });
    }

    private ClientConnection? Current => Volatile.Read(ref _client);

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            await Problems.Create(context, ErrorCodes.ValidationFailed, StatusCodes.Status400BadRequest,
                "A WebSocket upgrade is required.").ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var authentication = await AuthenticateAsync(socket, context.RequestAborted).ConfigureAwait(false);
        if (authentication is null)
            return;

        var connection = new ClientConnection(socket, authentication.Value.UserId, _logger, Volatile.Read(ref _outboundCapacity), Volatile.Read(ref _beforeSocketSendAsync));
        var holder = Interlocked.CompareExchange(ref _client, connection, null);
        if (holder is not null && authentication.Value.TakeOver && string.Equals(holder.UserId, connection.UserId, StringComparison.Ordinal))
        {
            holder.RequestTakeOver();
            try
            {
                await holder.Released.Task.WaitAsync(_takeOverBound, context.RequestAborted).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
            }

            holder = Interlocked.CompareExchange(ref _client, connection, null);
        }

        if (holder is not null)
        {
            try
            {
                var canTakeOver = string.Equals(holder.UserId, connection.UserId, StringComparison.Ordinal);
                await SendBusyAsync(socket, canTakeOver, context.RequestAborted).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    connection.Released.TrySetResult();
                }
            }

            return;
        }

        _logger.LogDebug("Presenter WebSocket authenticated for user {UserId}", connection.UserId);
        try
        {
            connection.EnqueueText(StateFrame(_presenter.Snapshot()));
            await ReceiveLoopAsync(socket, connection, context.RequestAborted).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        finally
        {
            // A writer fault must not relinquish ownership. Only the receive owner can perform cleanup, and the
            // slot stays held until presenter shutdown and the recorder attempt barrier have both completed.
            var owned = ReferenceEquals(Volatile.Read(ref _client), connection);
            try
            {
                if (owned)
                {
                    // StartAsync only means the command was queued until its task completes. In particular, do not
                    // inspect idle or retire the recorder until ObserveStartAsync has also attempted BeginAsync.
                    await connection.WaitForStartObservationAsync(_logger, StartObservationBound).ConfigureAwait(false);
                    if (_presenter.Snapshot().State != "idle")
                    {
                        await ObserveEndAsync(connection.TakeOverRequested).ConfigureAwait(false);
                    }

                    await connection.EndRecorderAsync().ConfigureAwait(false);
                    await connection.DetachAndDisposeRecorderAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    if (owned)
                    {
                        Interlocked.CompareExchange(ref _client, null, connection);
                    }

                    connection.Released.TrySetResult();
                }
            }
        }
    }

    private async Task<(string UserId, bool TakeOver)?> AuthenticateAsync(WebSocket socket, CancellationToken requestCancellation)
    {
        if (!_pendingAuth.Wait(0))
        {
            await CloseInvalidTicketAsync(socket).ConfigureAwait(false);
            return null;
        }

        var permitHeld = true;
        async Task RejectAsync()
        {
            if (permitHeld)
            {
                _pendingAuth.Release();
                permitHeld = false;
            }

            await CloseInvalidTicketAsync(socket).ConfigureAwait(false);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation);
            timeout.CancelAfter(_authFrameTimeout);
            var buffer = new byte[16 * 1024];
            using var frame = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (result.MessageType != WebSocketMessageType.Text || frame.Length + result.Count > MaxAuthenticationFrameBytes)
                {
                    await RejectAsync().ConfigureAwait(false);
                    return null;
                }

                frame.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            using var document = JsonDocument.Parse(frame.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || !string.Equals(type.GetString(), "auth", StringComparison.Ordinal)
                || !root.TryGetProperty("ticket", out var ticket)
                || ticket.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(ticket.GetString()))
            {
                await RejectAsync().ConfigureAwait(false);
                return null;
            }

            var userId = await _ticketStore.ClaimAsync(ticket.GetString()!, timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(userId))
            {
                await RejectAsync().ConfigureAwait(false);
                return null;
            }

            var takeOver = root.TryGetProperty("takeOver", out var takeOverProperty)
                && takeOverProperty.ValueKind == JsonValueKind.True;
            return (userId, takeOver);
        }
        catch (OperationCanceledException)
        {
            if (!requestCancellation.IsCancellationRequested)
                await RejectAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "WebSocket ticket authentication failed");
            await RejectAsync().ConfigureAwait(false);
            return null;
        }
        finally
        {
            if (permitHeld)
            {
                _pendingAuth.Release();
            }
        }
    }

    private static Task CloseInvalidTicketAsync(WebSocket socket) =>
        CloseServerInitiatedAsync(socket, (WebSocketCloseStatus)4401, "session.ticket_invalid");

    private static async Task CloseServerInitiatedAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(ServerCloseBound);
        try
        {
            await socket.CloseAsync(status, reason, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            socket.Abort();
        }
        catch (WebSocketException)
        {
            socket.Abort();
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, ClientConnection connection, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open)
        {
            using var frame = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                var maximum = result.MessageType == WebSocketMessageType.Binary ? MaxAudioFrameBytes : MaxTextCommandBytes;
                if (frame.Length + result.Count > maximum)
                {
                    await CloseServerInitiatedAsync(socket, WebSocketCloseStatus.MessageTooBig, "message too big").ConfigureAwait(false);
                    return;
                }

                frame.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                ObserveCommand(_presenter.SendAudioAsync(frame.ToArray(), cancellationToken), connection, "audio", false);
                continue;
            }

            HandleText(Encoding.UTF8.GetString(frame.ToArray()), connection, cancellationToken);
        }
    }

    private void HandleText(string text, ClientConnection connection, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            connection.EnqueueText(new { type = "error", message = "invalid JSON", code = "protocol" });
            return;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("type", out var typeValue) || typeValue.ValueKind != JsonValueKind.String)
            {
                connection.EnqueueText(new { type = "error", message = "unknown command: ", code = "protocol" });
                return;
            }

            var type = typeValue.GetString() ?? string.Empty;
            switch (type)
            {
                case "auth":
                    // The first auth frame is consumed by AuthenticateAsync. Keep later auth frames
                    // harmless for clients that retry their handshake after connecting.
                    _logger.LogDebug("WebSocket auth frame received for user {UserId}", connection.UserId);
                    return;
                case "start":
                    if (!document.RootElement.TryGetProperty("presentation", out var presentation) || presentation.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(presentation.GetString()))
                    {
                        connection.EnqueueText(new { type = "error", message = "start.presentation is required", code = "protocol" });
                        return;
                    }

                    int? fromIndex = document.RootElement.TryGetProperty("fromIndex", out var from) && from.TryGetInt32(out var value) ? value : null;
                    _logger.LogDebug("Starting presentation {PresentationId} for user {UserId}", presentation.GetString(), connection.UserId);
                    var recorder = connection.PrepareRecorder(_recorderFactory, _presenter);
                    // Once queued, a presenter command cannot be withdrawn. Its observer must outlive this
                    // request so disconnect cleanup sees the actual start and its recorder BeginAsync attempt.
                    connection.ObserveStart(ObserveStartAsync(
                        _presenter.StartAsync(presentation.GetString()!, fromIndex, connection.UserId, CancellationToken.None),
                        connection,
                        recorder));
                    return;
                case "next": ObserveCommand(_presenter.NextAsync(cancellationToken), connection, type, false); return;
                case "prev": ObserveCommand(_presenter.PrevAsync(cancellationToken), connection, type, false); return;
                case "goto":
                    var index = document.RootElement.TryGetProperty("index", out var candidate) && candidate.TryGetInt32(out var parsed) ? parsed : int.MinValue;
                    ObserveCommand(_presenter.GotoAsync(index, cancellationToken), connection, type, false);
                    return;
                case "pause": ObserveCommand(_presenter.PauseAsync(cancellationToken), connection, type, false); return;
                case "resume": ObserveCommand(_presenter.ResumeAsync(cancellationToken), connection, type, false); return;
                case "mute": ObserveCommand(_presenter.MuteAsync(cancellationToken), connection, type, false); return;
                case "unmute": ObserveCommand(_presenter.UnmuteAsync(cancellationToken), connection, type, false); return;
                case "end": ObserveCommand(_presenter.EndAsync(cancellationToken: cancellationToken), connection, type, false); return;
                case "ping": connection.EnqueueText(new { type = "pong" }); return;
                default: connection.EnqueueText(new { type = "error", message = $"unknown command: {type}", code = "protocol" }); return;
            }
        }
    }

    private void ObserveCommand(Task task, ClientConnection connection, string command, bool start)
    {
        _ = task.ContinueWith(completed =>
        {
            if (!completed.IsFaulted || completed.Exception is null)
            {
                return;
            }

            var exception = completed.Exception.GetBaseException();
            _logger.LogError(exception, "Presenter command {Command} failed", command);
            if (start)
            {
                connection.EnqueueText(new { type = "error", message = exception.Message, code = "start" });
            }
            else
            {
                connection.EnqueueText(new { type = "log", level = "error", message = $"{command} failed: {exception.Message}" });
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task ObserveStartAsync(Task<PresenterStartResult> task, ClientConnection connection, ISessionRecorder? recorder)
    {
        try
        {
            var result = await task.ConfigureAwait(false);
            if (result.Started)
            {
                if (recorder is not null)
                {
                    await recorder.BeginAsync(connection.UserId, result).ConfigureAwait(false);
                }
            }
            else if (recorder is not null)
            {
                await connection.RetireRecorderAsync(recorder).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            if (recorder is not null)
            {
                await connection.RetireRecorderAsync(recorder).ConfigureAwait(false);
            }

            _logger.LogError(exception, "Presenter command start failed");
            connection.EnqueueText(new { type = "error", message = exception.Message, code = "start" });
        }
    }

    private async Task ObserveEndAsync(bool resumable = false)
    {
        // EndAsync returns once the close is requested, with the presenter still "ending"; it turns idle only when
        // the loop handles the upstream close queued behind that command. Releasing the slot before then lets the
        // next browser's start slip past PrepareRecorder and run unrecorded, and lets the recorder finalisation
        // attempt finish before the upstream's Closed (billed seconds, close reason) has reached it.
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnState(PresenterSnapshot snapshot)
        {
            if (snapshot.State == "idle") idle.TrySetResult();
        }

        _presenter.State += OnState;
        try
        {
            await _presenter.EndAsync(resumable).ConfigureAwait(false);
            if (_presenter.Snapshot().State != "idle")
            {
                await idle.Task.WaitAsync(EndToIdleBound).ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Presenter was not idle {Bound} after browser disconnect; releasing the slot anyway", EndToIdleBound);
        }
        catch (Exception exception) { _logger.LogError(exception, "Presenter end after browser disconnect failed"); }
        finally { _presenter.State -= OnState; }
    }

    internal void ConfigureOutboundForTest(int capacity, Func<Task>? beforeSocketSendAsync)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        if (Current is not null)
        {
            throw new InvalidOperationException("Configure the outbound test seam before connecting a browser.");
        }

        _outboundCapacity = capacity;
        _beforeSocketSendAsync = beforeSocketSendAsync;
    }

    internal Task StopCurrentWriterForTestAsync() => Current?.StopWriterForTestAsync() ?? Task.CompletedTask;

    internal void ConfigureTakeOverBoundForTest(TimeSpan bound)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bound, TimeSpan.Zero);
        _takeOverBound = bound;
    }

    private static object StateFrame(PresenterSnapshot snapshot) => new
    {
        type = "state", snapshot.State, snapshot.PresentationId, snapshot.Title, snapshot.SlideIndex, snapshot.SlideCount,
        snapshot.Paused, snapshot.Muted, snapshot.SessionId, snapshot.ExpiresAt, snapshot.UsageSeconds, snapshot.AdvanceSilenceMs
    };

    private static async Task SendBusyAsync(WebSocket socket, bool canTakeOver, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { type = "error", message = BusyMessage, code = "busy", canTakeOver }, JsonOptions);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        await CloseServerInitiatedAsync(socket, (WebSocketCloseStatus)1013, "busy").ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_presenter is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class ClientConnection : IAsyncDisposable
    {
        private readonly WebSocket _socket;
        private readonly ILogger _logger;
        public string UserId { get; }
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Channel<OutboundMessage> _outbound;
        private readonly Func<Task>? _beforeSocketSendAsync;
        private readonly Task _writer;
        private readonly CancellationTokenSource _takeOverAbort = new();
        private int _failed;
        private int _takeOverRequested;
        private readonly object _recorderLock = new();
        private IPresenter? _recorderPresenter;
        private Action<PresenterClosed>? _runClosedHandler;
        private ISessionRecorder? _recorder;
        private bool _recorderActive;
        private readonly object _startLock = new();
        private Task? _startObservation;
        internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool TakeOverRequested => Volatile.Read(ref _takeOverRequested) != 0;

        public ClientConnection(WebSocket socket, string userId, ILogger logger, int outboundCapacity, Func<Task>? beforeSocketSendAsync)
        {
            _socket = socket;
            UserId = userId;
            _logger = logger;
            _beforeSocketSendAsync = beforeSocketSendAsync;
            _outbound = Channel.CreateBounded<OutboundMessage>(new BoundedChannelOptions(outboundCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
            _writer = Task.Run(WriteLoopAsync);
        }

        private async Task WriteLoopAsync()
        {
            try
            {
                await foreach (var message in _outbound.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
                {
                    if (_beforeSocketSendAsync is not null)
                    {
                        await _beforeSocketSendAsync().WaitAsync(_lifetime.Token).ConfigureAwait(false);
                    }

                    await _socket.SendAsync(message.Bytes, message.Binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, _lifetime.Token).ConfigureAwait(false);
                    if (message.CloseStatus is { } closeStatus)
                    {
                        await _socket.CloseOutputAsync(closeStatus, message.CloseReason!, _lifetime.Token).ConfigureAwait(false);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException exception)
            {
                _logger.LogDebug(exception, "Browser WebSocket writer stopped");
            }
            finally
            {
                if (Volatile.Read(ref _failed) != 0 && !TakeOverRequested && _socket.State == WebSocketState.Open)
                {
                    await CloseServerInitiatedAsync(_socket, (WebSocketCloseStatus)1011, "server cannot keep up").ConfigureAwait(false);
                }
            }
        }

        public void EnqueueText(object frame) => Enqueue(new OutboundMessage(JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions), false));

        public void EnqueueBinary(byte[] bytes) => Enqueue(new OutboundMessage(bytes, true));

        internal void RequestTakeOver()
        {
            if (Interlocked.CompareExchange(ref _takeOverRequested, 1, 0) != 0)
            {
                return;
            }

            Enqueue(new OutboundMessage(
                JsonSerializer.SerializeToUtf8Bytes(new { type = "error", message = "This presenter was taken over by another tab.", code = "taken_over" }, JsonOptions),
                false,
                (WebSocketCloseStatus)4409,
                "taken_over"));
            _ = AbortAfterTakeOverCloseAsync();
        }

        private async Task AbortAfterTakeOverCloseAsync()
        {
            try
            {
                await Task.Delay(ServerCloseBound, _takeOverAbort.Token).ConfigureAwait(false);
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseSent or WebSocketState.CloseReceived)
                {
                    _socket.Abort();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
                // The holder was already disposing when the take-over arrived; its cleanup releases the slot.
            }
        }

        private void Enqueue(OutboundMessage message)
        {
            if (_outbound.Writer.TryWrite(message))
            {
                return;
            }

            FailForBackpressure();
        }

        internal Task StopWriterForTestAsync()
        {
            _outbound.Writer.TryComplete();
            return _writer;
        }

        internal void ObserveStart(Task observation)
        {
            lock (_startLock)
            {
                // A second start can finish Started=false while the first is still loading. Retaining only the
                // latest task would let disconnect cleanup overtake the first recorder BeginAsync attempt.
                _startObservation = _startObservation is { IsCompleted: false } prior
                    ? Task.WhenAll(prior, observation)
                    : observation;
            }
        }

        internal async Task WaitForStartObservationAsync(ILogger logger, TimeSpan bound)
        {
            Task? observation;
            lock (_startLock)
            {
                observation = _startObservation;
            }

            if (observation is null)
            {
                return;
            }

            try
            {
                await observation.WaitAsync(bound).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                logger.LogWarning("Presenter start observation exceeded {Bound}; releasing the browser slot", bound);
            }
            catch (Exception exception)
            {
                // ObserveStartAsync normally handles and reports its own failure. Keep cleanup safe if an
                // unexpected observer failure escaped before it could do so.
                logger.LogError(exception, "Presenter start observation failed during browser disconnect cleanup");
            }
        }

        internal ISessionRecorder? PrepareRecorder(ISessionRecorderFactory factory, IPresenter presenter)
        {
            lock (_recorderLock)
            {
                // A second start while presenting must not replace or end the first run's recorder. The presenter
                // itself will return Started=false for that command.
                if (_recorderActive || !string.Equals(presenter.Snapshot().State, "idle", StringComparison.Ordinal))
                {
                    return null;
                }

                var prior = _recorder;
                if (prior is not null)
                {
                    DetachRunHandler();
                    prior.Detach();
                    _ = FinishPriorRecorderAsync(prior);
                }

                _recorder = factory.Create();
                _recorder.Attach(presenter);
                _recorderPresenter = presenter;
                _runClosedHandler = _ => MarkRunClosed();
                presenter.Closed += _runClosedHandler;
                _recorderActive = true;
                return _recorder;
            }
        }

        internal async Task RetireRecorderAsync(ISessionRecorder recorder)
        {
            var owns = false;
            lock (_recorderLock)
            {
                owns = ReferenceEquals(_recorder, recorder);
            }

            if (!owns)
            {
                return;
            }

            await recorder.EndAsync().ConfigureAwait(false);
            lock (_recorderLock)
            {
                if (ReferenceEquals(_recorder, recorder))
                {
                    DetachRunHandler();
                    recorder.Detach();
                    _recorder = null;
                    _recorderActive = false;
                }
            }

            await recorder.DisposeAsync().ConfigureAwait(false);
        }

        internal async Task EndRecorderAsync()
        {
            ISessionRecorder? recorder;
            lock (_recorderLock)
            {
                recorder = _recorder;
            }

            if (recorder is not null)
            {
                await recorder.EndAsync().ConfigureAwait(false);
            }
        }

        internal async Task DetachAndDisposeRecorderAsync()
        {
            ISessionRecorder? recorder;
            lock (_recorderLock)
            {
                recorder = _recorder;
                _recorder = null;
                _recorderActive = false;
                DetachRunHandler();
                recorder?.Detach();
            }

            if (recorder is not null)
            {
                await recorder.DisposeAsync().ConfigureAwait(false);
            }
        }

        private void MarkRunClosed()
        {
            lock (_recorderLock)
            {
                _recorderActive = false;
            }
        }

        private void DetachRunHandler()
        {
            if (_recorderPresenter is not null && _runClosedHandler is not null)
            {
                _recorderPresenter.Closed -= _runClosedHandler;
            }

            _recorderPresenter = null;
            _runClosedHandler = null;
        }

        private static async Task FinishPriorRecorderAsync(ISessionRecorder recorder)
        {
            try
            {
                await recorder.EndAsync().ConfigureAwait(false);
                await recorder.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // A prior run is already detached; its recorder must never affect a new presenter run.
            }
        }

        internal void FailForBackpressure()
        {
            if (Interlocked.CompareExchange(ref _failed, 1, 0) == 0)
            {
                // No waiters and no eviction: the writer drains the bounded queue then sends one 1011 close.
                _outbound.Writer.TryComplete();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _outbound.Writer.TryComplete();
            _lifetime.Cancel();
            try { await _writer.ConfigureAwait(false); } catch (OperationCanceledException) { }
            _takeOverAbort.Cancel();
            _takeOverAbort.Dispose();
            _lifetime.Dispose();
        }

        private sealed record OutboundMessage(byte[] Bytes, bool Binary, WebSocketCloseStatus? CloseStatus = null, string? CloseReason = null);
    }
}

public static class PresenterBridgeEndpoints
{
    public static IEndpointRouteBuilder MapPresenterBridge(this IEndpointRouteBuilder endpoints)
    {
        endpoints.Map("/ws", (HttpContext context, PresenterBridge bridge) => bridge.HandleAsync(context))
            .WithName("PresenterBridge")
            .ExcludeFromDescription();
        return endpoints;
    }
}

public static class PresenterRegistration
{
    public static IServiceCollection AddPresenterBridge(this IServiceCollection services)
    {
        services.AddSingleton<PresenterBridge>();
        return services;
    }
}
