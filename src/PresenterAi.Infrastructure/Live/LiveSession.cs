using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PresenterAi.Application.Presenting;

namespace PresenterAi.Infrastructure.Live;

public sealed class LiveSession : ILiveSession, IAsyncDisposable
{
    private const int BytesPerMs = 48;
    private const int PumpIntervalMs = 20;
    private const int PumpSlackMs = 120;
    private const int MaxPumpFrames = 25;
    private static readonly byte[] SilenceFrame = new byte[PumpIntervalMs * BytesPerMs];

    private readonly UpstreamRoute _route;
    private readonly LiveSessionConfig _config;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LiveSession> _logger;
    private readonly LiveSessionOptions _options;
    private ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _connection = new();
    private readonly Channel<OutboundFrame> _outbound = Channel.CreateUnbounded<OutboundFrame>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly TaskCompletionSource<LiveSessionInfo> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<LiveCloseResult> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<LiveStartupException> _delegationRejected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? _sendLoop;
    private Task? _receiveLoop;
    private Task? _pumpLoop;
    private PeriodicTimer? _pump;
    private LiveSessionInfo? _session;
    private long _pumpStartedAt;
    private long _sentMs;
    private long _silenceMs;
    private int _state = (int)LiveSessionState.Idle;
    private int _finished;
    private int _eventSequence;
    private int _audioDeltas;
    private bool _useClientDelegation;
    private bool _delegationRetryUsed;

    public LiveSession(
        UpstreamRoute route,
        LiveSessionConfig config,
        TimeProvider timeProvider,
        ILogger<LiveSession> logger,
        LiveSessionOptions? options = null)
    {
        _route = route;
        _config = config;
        _timeProvider = timeProvider;
        _logger = logger;
        _options = options ?? new LiveSessionOptions();
    }

    public event Action<LiveSessionInfo>? Started;
    public event Action<ReadOnlyMemory<byte>, long?, long?>? Audio;
    public event Action<string, string, long?, long?>? Transcript;
    public event Action<string, string?, JsonElement>? Appended;
    public event Action<double, double?>? Usage;
    public event Action<JsonElement>? Delegation;
    public event Action<JsonElement>? UpstreamError;
    public event Action<string>? Warning;
    public event Action<string, string>? DelegatedResponseFinished;
    public event Action<string, string, string, string>? ToolCallRequested;
    public event Action<string, string, string>? HostedToolActivity;
    public event Action<string, double?>? Closed;

    public LiveSessionState State => (LiveSessionState)Volatile.Read(ref _state);

    public string? Id => _session?.Id;

    public string? Name => _route.Name;

    public long SilenceMs => Volatile.Read(ref _silenceMs);

    public async Task<LiveSessionInfo> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _state, (int)LiveSessionState.Connecting, (int)LiveSessionState.Idle)
            != (int)LiveSessionState.Idle)
        {
            throw new InvalidOperationException($"LiveSession.ConnectAsync: state is {State}");
        }

        try
        {
            await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var completed = await Task.WhenAny(_started.Task, _delegationRejected.Task)
                    .WaitAsync(_options.HandshakeTimeout, _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                if (completed == _started.Task)
                {
                    return await _started.Task.ConfigureAwait(false);
                }

                var rejection = await _delegationRejected.Task.ConfigureAwait(false);
                _delegationRetryUsed = true;
                _useClientDelegation = true;
                Warning?.Invoke($"delegation: backend unavailable ({rejection.Code}); answering from the deck only");
                await ResetConnectionForDelegationRetryAsync().ConfigureAwait(false);
                await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (TimeoutException) when (_started.Task.IsCompleted is false)
        {
            _socket.Abort();
            Finish("connection_lost", null);
            throw new TimeoutException($"GPT-Live handshake timed out after {_options.HandshakeTimeout.TotalMilliseconds:0} ms");
        }
        catch
        {
            if (Volatile.Read(ref _finished) == 0)
            {
                _socket.Abort();
                Finish("connection_lost", null);
            }

            throw;
        }
    }

    private async Task OpenConnectionAsync(CancellationToken cancellationToken)
    {
        using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _connection.Token, cancellationToken);
        foreach (var header in _route.Headers)
        {
            _socket.Options.SetRequestHeader(header.Key, header.Value);
        }

        await _socket.ConnectAsync(_route.LiveUrl, connectCancellation.Token)
            .WaitAsync(_options.HandshakeTimeout, _timeProvider, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Upstream socket open, sending session.start");
        _sendLoop = SendLoopAsync(_connection.Token);
        _receiveLoop = ReceiveLoopAsync(_connection.Token);
        Enqueue(new JsonFrame(CreateStartEvent(), AllowConnecting: true));
    }

    private async Task ResetConnectionForDelegationRetryAsync()
    {
        _connection.Cancel();
        _socket.Abort();
        var loops = new[] { _sendLoop, _receiveLoop }.Where(task => task is not null).Cast<Task>();
        try
        {
            await Task.WhenAll(loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _socket.Dispose();
        _connection.Dispose();
        _socket = new ClientWebSocket();
        _connection = new CancellationTokenSource();
        _delegationRejected = new TaskCompletionSource<LiveStartupException>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public string? AppendInstructions(string content, string? eventId = null, string? delegationId = null)
    {
        return Append("instructions", content, eventId, delegationId);
    }

    public string? AppendThinking(string content, string? eventId = null, string? delegationId = null)
    {
        return Append("thinking", content, eventId, delegationId);
    }

    public string? AppendCommentary(string content, string? eventId = null, string? delegationId = null)
    {
        return Append("commentary", content, eventId, delegationId);
    }

    public bool SubmitToolOutput(string callId, string output)
    {
        if (State != LiveSessionState.Open)
        {
            return false;
        }

        var payload = new JsonObject
        {
            ["type"] = "response.item.create",
            ["event_id"] = NextEventId("tool-res"),
            ["item"] = new JsonObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = callId,
                ["output"] = output
            }
        };
        return Enqueue(new JsonFrame(payload));
    }

    public bool ContinueResponses()
    {
        if (State != LiveSessionState.Open)
        {
            return false;
        }

        var payload = new JsonObject
        {
            ["type"] = "response.create",
            ["event_id"] = NextEventId("continue")
        };
        return Enqueue(new JsonFrame(payload));
    }

    public bool Mute()
    {
        return SendCommand("session.input_audio.mute", NextEventId("mute"));
    }

    public bool Unmute()
    {
        return SendCommand("session.input_audio.unmute", NextEventId("unmute"));
    }

    public bool SendAudio(ReadOnlyMemory<byte> pcm16)
    {
        if (State != LiveSessionState.Open)
        {
            return false;
        }

        var length = pcm16.Length & ~1;
        if (length == 0)
        {
            return false;
        }

        return Enqueue(new AudioFrame(pcm16[..length].ToArray(), IsSilence: false));
    }

    public async Task<LiveCloseResult> CloseAsync()
    {
        if (Volatile.Read(ref _finished) != 0)
        {
            return await _closed.Task.ConfigureAwait(false);
        }

        if (State == LiveSessionState.Idle)
        {
            Finish("close_requested", null);
            return await _closed.Task.ConfigureAwait(false);
        }

        var priorState = (LiveSessionState)Interlocked.Exchange(ref _state, (int)LiveSessionState.Closing);
        if (priorState == LiveSessionState.Open)
        {
            SendCommand("session.close", NextEventId("close"), allowClosing: true);
        }

        try
        {
            return await _closed.Task.WaitAsync(_options.CloseTimeout, _timeProvider).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("No session.closed within timeout; terminating socket (final usage unconfirmed)");
            _socket.Abort();
            Finish("connection_lost", null);
            return await _closed.Task.ConfigureAwait(false);
        }
    }

    public void Terminate()
    {
        _socket.Abort();
        Finish("connection_lost", null);
    }

    public async ValueTask DisposeAsync()
    {
        Terminate();
        var loops = new[] { _sendLoop, _receiveLoop, _pumpLoop }.Where(task => task is not null).Cast<Task>();
        try
        {
            await Task.WhenAll(loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _pump?.Dispose();
        _socket.Dispose();
        _connection.Dispose();
        _lifetime.Dispose();
        _logger.LogInformation(
            "Upstream socket disposed: session={Id} route={Route} state={State}",
            Id,
            _route.Name,
            State);
    }

    private async Task SendLoopAsync(CancellationToken connectionToken)
    {
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, connectionToken);
            await foreach (var frame in _outbound.Reader.ReadAllAsync(cancellation.Token).ConfigureAwait(false))
            {
                if (_socket.State != WebSocketState.Open)
                {
                    continue;
                }

                switch (frame)
                {
                    case PumpFrame:
                        await FillSilenceToNowAsync().ConfigureAwait(false);
                        break;
                    case JsonFrame json when json.AllowConnecting || State is LiveSessionState.Open or LiveSessionState.Closing:
                        await SendJsonAsync(json.Event).ConfigureAwait(false);
                        break;
                    case AudioFrame audio when State == LiveSessionState.Open:
                        await SendAudioFrameAsync(audio).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || connectionToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException exception) when (!connectionToken.IsCancellationRequested)
        {
            _logger.LogInformation(exception, "Upstream socket send failed");
            Finish("connection_lost", null);
        }
        catch (WebSocketException) when (connectionToken.IsCancellationRequested)
        {
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken connectionToken)
    {
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, connectionToken);
            var buffer = new byte[16 * 1024];
            while (!cancellation.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Finish("connection_lost", null);
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    HandleEvent(message.ToArray());
                    if (_delegationRejected.Task.IsCompleted)
                    {
                        // ConnectAsync reconnects in client mode; a close that follows the rejection must not finish
                        // the session first.
                        return;
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    Audio?.Invoke(message.ToArray(), null, null);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || connectionToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException exception) when (!connectionToken.IsCancellationRequested)
        {
            _logger.LogInformation(exception, "Upstream socket receive failed");
            Finish("connection_lost", null);
        }
        catch (WebSocketException) when (connectionToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (!connectionToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Upstream receive loop failed");
            Finish("connection_lost", null);
        }
        catch (Exception) when (connectionToken.IsCancellationRequested)
        {
        }
    }

    private async Task PumpLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PumpIntervalMs), _timeProvider);
            _pump = timer;
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (State == LiveSessionState.Open)
                {
                    Enqueue(new PumpFrame());
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _pump = null;
        }
    }

    private async Task FillSilenceToNowAsync()
    {
        if (State != LiveSessionState.Open)
        {
            return;
        }

        var elapsedMs = (long)_timeProvider.GetElapsedTime(_pumpStartedAt).TotalMilliseconds;
        var frames = 0;
        while (elapsedMs - _sentMs > PumpSlackMs && frames < MaxPumpFrames && State == LiveSessionState.Open)
        {
            await SendAudioFrameAsync(new AudioFrame(SilenceFrame, IsSilence: true)).ConfigureAwait(false);
            frames++;
        }
    }

    private async Task SendAudioFrameAsync(AudioFrame audio)
    {
        var payload = new JsonObject
        {
            ["type"] = "session.input_audio.append",
            ["audio"] = Convert.ToBase64String(audio.Bytes)
        };
        await SendJsonAsync(payload).ConfigureAwait(false);
        _sentMs += audio.Bytes.Length / BytesPerMs;
        if (audio.IsSilence)
        {
            _silenceMs += audio.Bytes.Length / BytesPerMs;
        }
    }

    private async Task SendJsonAsync(JsonObject message)
    {
        var type = message["type"]?.GetValue<string>() ?? "unknown";
        var eventId = message["event_id"]?.GetValue<string>();
        if (_options.LogEvents)
        {
            _logger.LogInformation(">> {Event}", message.ToJsonString());
        }
        else
        {
            _logger.LogInformation(">> {Type}{EventId}", type, eventId is null ? string.Empty : $" {eventId}");
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _lifetime.Token)
            .ConfigureAwait(false);
    }

    private void HandleEvent(byte[] bytes)
    {
        JsonElement message;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            message = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            _logger.LogWarning("Upstream sent non-JSON frame, ignored");
            return;
        }

        var type = GetString(message, "type") ?? "unknown";
        if (type == "session.output_audio.delta")
        {
            var count = Interlocked.Increment(ref _audioDeltas);
            if (count % 100 == 1)
            {
                _logger.LogInformation("Output audio delta #{Count} ({Start}–{End} ms)", count, GetInt64(message, "start_ms"), GetInt64(message, "end_ms"));
            }

            var delta = GetString(message, "delta");
            if (delta is not null)
            {
                Audio?.Invoke(Convert.FromBase64String(delta), GetInt64(message, "start_ms"), GetInt64(message, "end_ms"));
            }

            return;
        }

        if (_options.LogEvents)
        {
            _logger.LogInformation("<< {Event}", message.GetRawText());
        }
        else if (!type.EndsWith("transcript.delta", StringComparison.Ordinal))
        {
            _logger.LogInformation("<< {Type}{EventId}", type, EventIdSuffix(message));
        }

        switch (type)
        {
            case "session.started":
                HandleStarted(message);
                break;
            case "session.input_transcript.delta":
                Transcript?.Invoke("user", GetString(message, "delta") ?? string.Empty, GetInt64(message, "start_ms"), GetInt64(message, "end_ms"));
                break;
            case "session.output_transcript.delta":
                Transcript?.Invoke("assistant", GetString(message, "delta") ?? string.Empty, GetInt64(message, "start_ms"), GetInt64(message, "end_ms"));
                break;
            case "session.instructions.appended":
            case "session.thinking.appended":
            case "session.commentary.appended":
                Appended?.Invoke(type.Split('.')[1], GetString(message, "client_event_id"), message);
                break;
            case "session.usage.updated":
                var usage = GetProperty(message, "usage");
                var contextWindow = GetProperty(message, "context_window");
                Usage?.Invoke(GetDouble(usage, "seconds") ?? 0, GetDouble(contextWindow, "usage_ratio"));
                break;
            case "session.delegation.created":
                Delegation?.Invoke(message);
                break;
            case "response.event":
                HandleResponseEvent(message);
                break;
            case "error":
                var error = GetProperty(message, "error") ?? message;
                _logger.LogWarning("Upstream error: {Error}", error.GetRawText());
                LiveStartupException? startup = null;
                if (State == LiveSessionState.Connecting)
                {
                    var code = GetString(error, "code") ?? "connect";
                    var upstreamMessage = GetString(error, "message") ?? "unknown";
                    startup = new LiveStartupException(code, error, $"GPT-Live startup error: {upstreamMessage}");
                    if (!_useClientDelegation && !_delegationRetryUsed && IsDelegationStartupError(error))
                    {
                        // The client-mode retry recovers this and reports it as a warning, so it is not raised as an
                        // upstream error.
                        _delegationRejected.TrySetResult(startup);
                        break;
                    }
                }

                UpstreamError?.Invoke(error);
                if (startup is not null)
                {
                    // Finish (with the startup failure) before ConnectAsync is woken: its catch block finishes
                    // with "connection_lost" when nothing has finished yet, and it may resume on another thread.
                    _socket.Abort();
                    Finish("startup_error", null, startup);
                }

                break;
            case "session.closed":
                Finish(GetString(message, "reason") ?? "close_requested", GetDouble(GetProperty(message, "usage"), "seconds"));
                break;
        }
    }

    private void HandleStarted(JsonElement message)
    {
        var session = GetProperty(message, "session") ?? message;
        var delegationMode = _useClientDelegation || string.IsNullOrWhiteSpace(_route.DelegationModel)
            ? "client"
            : "responses";
        var info = new LiveSessionInfo(GetString(session, "id"), GetString(session, "model"), GetInt64(session, "expires_at"), session, delegationMode);
        _session = info;
        Volatile.Write(ref _state, (int)LiveSessionState.Open);
        _pumpStartedAt = _timeProvider.GetTimestamp();
        _sentMs = 0;
        _pumpLoop = _options.SilencePump ? PumpLoopAsync() : null;
        _logger.LogInformation("Session started: id={Id} model={Model} expires_at={ExpiresAt} delegation={DelegationMode}", info.Id, info.Model, info.ExpiresAt, info.DelegationMode);
        Started?.Invoke(info);
        _started.TrySetResult(info);
    }

    private void HandleResponseEvent(JsonElement envelope)
    {
        var delegationId = GetString(envelope, "delegation_id") ?? "unknown";
        var responseEvent = GetProperty(envelope, "event");
        var type = responseEvent is { } nested ? GetString(nested, "type") : null;
        if (type == "response.output_item.done")
        {
            var item = responseEvent is { } resEv ? GetProperty(resEv, "item") : null;
            var itemType = item is { } it ? GetString(it, "type") : null;
            if (itemType == "web_search_call")
            {
                HostedToolActivity?.Invoke(delegationId, "web_search", item is { } searchItem ? GetString(searchItem, "status") ?? "done" : "done");
            }
            if (itemType == "function_call" && item is { } fnItem)
            {
                var callId = GetString(fnItem, "call_id") ?? string.Empty;
                var name = GetString(fnItem, "name") ?? string.Empty;
                var arguments = GetString(fnItem, "arguments") ?? "{}";
                _logger.LogInformation("Tool call requested: delegation={DelegationId} call={CallId} name={ToolName}", delegationId, callId, name);
                ToolCallRequested?.Invoke(delegationId, callId, name, arguments);
            }
        }
        else if (type == "response.output_item.added")
        {
            var item = responseEvent is { } resEv ? GetProperty(resEv, "item") : null;
            if (item is { } searchItem && GetString(searchItem, "type") == "web_search_call")
                HostedToolActivity?.Invoke(delegationId, "web_search", GetString(searchItem, "status") ?? "started");
        }
        else if (type == "response.completed")
        {
            _logger.LogInformation("Delegated response completed: id={DelegationId}", delegationId);
            DelegatedResponseFinished?.Invoke(delegationId, type);
        }
        else if (type is not null && (type.Contains("error", StringComparison.OrdinalIgnoreCase)
            || type.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || type.Contains("incomplete", StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("Delegated response failed: id={DelegationId} type={Type}", delegationId, type);
            DelegatedResponseFinished?.Invoke(delegationId, type);
        }
    }

    private static bool IsDelegationStartupError(JsonElement error)
    {
        var param = GetString(error, "param");
        if (param?.StartsWith("session.delegation", StringComparison.Ordinal) is true)
        {
            return true;
        }

        var code = GetString(error, "code") ?? string.Empty;
        var message = GetString(error, "message") ?? string.Empty;
        return code.Contains("delegation", StringComparison.OrdinalIgnoreCase)
            || code.Contains("tools", StringComparison.OrdinalIgnoreCase)
            || message.Contains("delegation", StringComparison.OrdinalIgnoreCase)
            || message.Contains("tools", StringComparison.OrdinalIgnoreCase);
    }

    private string? Append(string kind, string content, string? eventId, string? delegationId)
    {
        var text = (content ?? string.Empty).Trim();
        if (text.Length == 0 || State != LiveSessionState.Open)
        {
            return null;
        }

        var id = eventId ?? NextEventId(kind);
        var payload = new JsonObject
        {
            ["type"] = $"session.{kind}.append",
            ["event_id"] = id,
            ["delegation_id"] = delegationId,
            ["content"] = text
        };
        return Enqueue(new JsonFrame(payload)) ? id : null;
    }

    private bool SendCommand(string type, string eventId, bool allowClosing = false)
    {
        if (State != LiveSessionState.Open && !(allowClosing && State == LiveSessionState.Closing))
        {
            return false;
        }

        return Enqueue(new JsonFrame(new JsonObject { ["type"] = type, ["event_id"] = eventId }, AllowClosing: allowClosing));
    }

    private JsonObject CreateStartEvent()
    {
        return new JsonObject
        {
            ["type"] = "session.start",
            ["event_id"] = NextEventId("start"),
            ["session"] = new JsonObject
            {
                ["model"] = _config.Model,
                ["instructions"] = _config.Instructions,
                ["audio"] = new JsonObject { ["output"] = new JsonObject { ["voice"] = _config.Voice } },
                ["delegation"] = CreateDelegation()
            }
        };
    }

    private JsonObject CreateDelegation()
    {
        if (_useClientDelegation || string.IsNullOrWhiteSpace(_route.DelegationModel))
        {
            return new JsonObject { ["type"] = "client" };
        }

        var responses = new JsonObject
        {
            ["model"] = _route.DelegationModel,
            ["instructions"] = _config.DelegationInstructions ?? $"Answer audience questions about the talk titled {_config.PresentationTitle ?? "the presentation"} in one to three short spoken sentences; if unsure, say so.",
            ["reasoning"] = new JsonObject { ["effort"] = "low" },
            ["service_tier"] = "priority",
            ["text"] = new JsonObject { ["verbosity"] = "low" }
        };

        if (_config.Tools is { Count: > 0 } || _config.HostedTools is { Count: > 0 })
        {
            responses["tools"] = new JsonArray((_config.Tools ?? []).Concat(_config.HostedTools ?? []).Select(t => (JsonNode)t.DeepClone()).ToArray());
            responses["tool_choice"] = "auto";
            responses["parallel_tool_calls"] = false;
        }

        return new JsonObject
        {
            ["type"] = "responses",
            ["responses"] = responses
        };
    }

    private bool Enqueue(OutboundFrame frame)
    {
        return Volatile.Read(ref _finished) == 0 && _outbound.Writer.TryWrite(frame);
    }

    private string NextEventId(string prefix)
    {
        return $"{prefix}-{Interlocked.Increment(ref _eventSequence)}";
    }

    private void Finish(string reason, double? seconds, Exception? startupFailure = null)
    {
        if (Interlocked.CompareExchange(ref _finished, 1, 0) != 0)
        {
            return;
        }

        Volatile.Write(ref _state, (int)LiveSessionState.Closed);
        _lifetime.Cancel();
        var result = new LiveCloseResult(reason, seconds);
        _closed.TrySetResult(result);
        _started.TrySetException(startupFailure ?? new InvalidOperationException("upstream closed before session.started"));
        _logger.LogInformation("Session closed: reason={Reason} seconds={Seconds} (silence inserted: {SilenceMs} ms)", reason, seconds, _silenceMs);
        Closed?.Invoke(reason, seconds);
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static long? GetInt64(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) && property.TryGetInt64(out var value)
            ? value
            : null;
    }

    private static double? GetDouble(JsonElement? element, string name)
    {
        return element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) && property.TryGetDouble(out var number)
            ? number
            : null;
    }

    private static JsonElement? GetProperty(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) ? property : null;
    }

    private static string EventIdSuffix(JsonElement message)
    {
        var eventId = GetString(message, "client_event_id");
        return eventId is null ? string.Empty : $" {eventId}";
    }

    private abstract record OutboundFrame;
    private sealed record JsonFrame(JsonObject Event, bool AllowConnecting = false, bool AllowClosing = false) : OutboundFrame;
    private sealed record AudioFrame(byte[] Bytes, bool IsSilence) : OutboundFrame;
    private sealed record PumpFrame : OutboundFrame;
}
