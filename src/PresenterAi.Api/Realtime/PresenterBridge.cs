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
using PresenterAi.Infrastructure.Redis;

namespace PresenterAi.Api.Realtime;

/// <summary>Owns the process-wide browser slot and routes presenter events to its current connection.</summary>
public sealed class PresenterBridge : IAsyncDisposable
{
    private const string BusyMessage = "Another presenter page is already connected. Close it first.";
    private readonly IPresenter _presenter;
    private readonly ILogger<PresenterBridge> _logger;
    private readonly ITicketStore _ticketStore;
    private readonly TimeSpan _authFrameTimeout;
    private readonly SemaphoreSlim _pendingAuth;
    private ClientConnection? _client;
    private int _outboundCapacity = 500;
    private Func<Task>? _beforeSocketSendAsync;

    public PresenterBridge(
        IPresenter presenter,
        ILogger<PresenterBridge> logger,
        ITicketStore ticketStore,
        IOptions<SessionRedisOptions> sessionOptions)
    {
        _presenter = presenter;
        _logger = logger;
        _ticketStore = ticketStore;
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
        var userId = await AuthenticateAsync(socket, context.RequestAborted).ConfigureAwait(false);
        if (userId is null)
            return;

        var connection = new ClientConnection(socket, userId, _logger, Volatile.Read(ref _outboundCapacity), Volatile.Read(ref _beforeSocketSendAsync));
        if (Interlocked.CompareExchange(ref _client, connection, null) is not null)
        {
            await SendBusyAsync(socket, context.RequestAborted).ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
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
            // A writer fault must not relinquish ownership. Only the receive owner's successful CAS can end it.
            var owned = Interlocked.CompareExchange(ref _client, null, connection) == connection;
            await connection.DisposeAsync().ConfigureAwait(false);
            if (owned && _presenter.Snapshot().State != "idle")
            {
                await ObserveEndAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<string?> AuthenticateAsync(WebSocket socket, CancellationToken requestCancellation)
    {
        if (!_pendingAuth.Wait(0))
        {
            await CloseInvalidTicketAsync(socket).ConfigureAwait(false);
            return null;
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
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    await CloseInvalidTicketAsync(socket).ConfigureAwait(false);
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
                await CloseInvalidTicketAsync(socket).ConfigureAwait(false);
                return null;
            }

            var userId = await _ticketStore.ClaimAsync(ticket.GetString()!, timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(userId))
            {
                await CloseInvalidTicketAsync(socket).ConfigureAwait(false);
                return null;
            }

            return userId;
        }
        catch (OperationCanceledException)
        {
            if (!requestCancellation.IsCancellationRequested)
                await CloseInvalidTicketAsync(socket).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "WebSocket ticket authentication failed");
            await CloseInvalidTicketAsync(socket).ConfigureAwait(false);
            return null;
        }
        finally
        {
            _pendingAuth.Release();
        }
    }

    private static async Task CloseInvalidTicketAsync(WebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;

        try
        {
            await socket.CloseAsync((WebSocketCloseStatus)4401, "session.ticket_invalid", CancellationToken.None).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
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
                    ObserveCommand(_presenter.StartAsync(presentation.GetString()!, fromIndex, cancellationToken), connection, "start", true);
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
                case "end": ObserveCommand(_presenter.EndAsync(cancellationToken), connection, type, false); return;
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

    private async Task ObserveEndAsync()
    {
        try { await _presenter.EndAsync().ConfigureAwait(false); }
        catch (Exception exception) { _logger.LogError(exception, "Presenter end after browser disconnect failed"); }
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

    private static object StateFrame(PresenterSnapshot snapshot) => new
    {
        type = "state", snapshot.State, snapshot.PresentationId, snapshot.Title, snapshot.SlideIndex, snapshot.SlideCount,
        snapshot.Paused, snapshot.Muted, snapshot.SessionId, snapshot.ExpiresAt, snapshot.UsageSeconds, snapshot.AdvanceSilenceMs
    };

    private static async Task SendBusyAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { type = "error", message = BusyMessage, code = "busy" }, JsonOptions);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        await socket.CloseAsync((WebSocketCloseStatus)1013, "busy", cancellationToken).ConfigureAwait(false);
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
        private int _failed;

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
                if (Volatile.Read(ref _failed) != 0 && _socket.State == WebSocketState.Open)
                {
                    try { await _socket.CloseAsync((WebSocketCloseStatus)1011, "server cannot keep up", CancellationToken.None).ConfigureAwait(false); }
                    catch (WebSocketException) { }
                }
            }
        }

        public void EnqueueText(object frame) => Enqueue(new OutboundMessage(JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions), false));

        public void EnqueueBinary(byte[] bytes) => Enqueue(new OutboundMessage(bytes, true));

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
            _lifetime.Dispose();
        }

        private sealed record OutboundMessage(byte[] Bytes, bool Binary);
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
