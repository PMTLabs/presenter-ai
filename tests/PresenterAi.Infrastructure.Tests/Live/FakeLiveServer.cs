using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace PresenterAi.Infrastructure.Tests.Live;

public sealed class FakeLiveServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<WebSocket, byte> _connections = new();
    private readonly object _receivedLock = new();
    private readonly object _headersLock = new();
    private IReadOnlyDictionary<string, string>? _headers;

    private FakeLiveServer(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int AudioDeltasPerAppend { get; set; } = 3;

    public int DeltaGapMs { get; set; } = 20;

    public int StartDelayMs { get; set; }

    public bool IgnoreClose { get; set; }

    public int DelegationStartRejections { get; set; }

    public int ToolsStartRejections { get; set; }

    public bool CloseAfterDelegationRejection { get; set; }

    public string SessionId { get; set; } = "sess_fake";

    /// <summary>Plan 011 T5: when true, every recorded append also carries its base64 <c>audio</c> payload.</summary>
    public bool KeepAudio { get; set; }

    /// <summary>Plan 011 T5: when set, <c>session.start</c> waits on it before answering (e.g. to hold a reconnect).</summary>
    public TaskCompletionSource? StartGate { get; set; }

    /// <summary>How many <c>session.start</c> events reached the server (counted before any <see cref="StartGate"/> wait).</summary>
    public int StartCount => Volatile.Read(ref _startCount);

    /// <summary>
    /// Plan 011 T5: when set, the <c>session.input_audio.unmuted</c> ack is sent only after it completes. Reads go on
    /// meanwhile, so an append sent before the ack is still received and recorded.
    /// </summary>
    public TaskCompletionSource? UnmuteAckGate { get; set; }

    /// <summary>Plan 011 T5: after this many burst-sized (non-960-byte) appends, reads wait on <see cref="ReadGate"/>.</summary>
    public int? GateReadsAfterBurstAppends { get; set; }

    public TaskCompletionSource ReadGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Plan 011 T5 (P-16): after a burst, answers with this many 20 ms <see cref="AnswerDelta"/> frames, each sent only
    /// once the server has received 20 ms more unmuted input audio, emulating an upstream whose output is paced by its
    /// input clock (it stalls when input stops or is muted).
    /// </summary>
    public int PacedAnswerFrames { get; set; }

    public int AnswerFramesSent => Volatile.Read(ref _answerFramesSent);

    private int _startCount;
    private int _answerFramesSent;
    private int _burstAppends;
    private bool _inputMuted;
    private long _inputMs;
    private bool _burstSeen;
    private bool _answerStarted;

    public int Port { get; private set; }

    public string Url => $"ws://127.0.0.1:{Port}/v1/live/sessions";

    public int ConnectionCount => _connections.Count;

    public List<JsonObject> Received { get; } = [];

    public IReadOnlyDictionary<string, string>? Headers
    {
        get
        {
            lock (_headersLock)
            {
                return _headers;
            }
        }
    }

    public static async Task<FakeLiveServer> StartAsync(int audioDeltasPerAppend = 3, int deltaGapMs = 20, string sessionId = "sess_fake")
    {
        var builder = WebApplication.CreateSlimBuilder();
        // Referencing this fixture from API tests makes the API appsettings visible to the slim host;
        // remove those ambient endpoints so the explicit ephemeral listener remains the only address.
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var server = new FakeLiveServer(app, 0)
        {
            AudioDeltasPerAppend = audioDeltasPerAppend,
            DeltaGapMs = deltaGapMs,
            SessionId = sessionId
        };
        app.UseWebSockets();
        app.Map("/v1/live/sessions", server.HandleConnectionAsync);
        await app.StartAsync();
        var address = app.Urls.Single();
        server.Port = new Uri(address).Port;
        return server;
    }

    public void DropAll()
    {
        foreach (var socket in _connections.Keys)
        {
            socket.Abort();
        }
    }

    public async ValueTask DisposeAsync()
    {
        DropAll();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public Task SendEventAsync(JsonObject message)
    {
        var socket = _connections.Keys.Single();
        return SendAsync(socket, message, CancellationToken.None);
    }

    public Task SendFunctionCallAsync(string delegationId, string callId, string name, string arguments)
    {
        return SendEventAsync(new JsonObject
        {
            ["type"] = "response.event",
            ["delegation_id"] = delegationId,
            ["event"] = new JsonObject
            {
                ["type"] = "response.output_item.done",
                ["item"] = new JsonObject
                {
                    ["type"] = "function_call",
                    ["call_id"] = callId,
                    ["name"] = name,
                    ["arguments"] = arguments
                }
            }
        });
    }

    public IReadOnlyList<JsonObject> ReceivedSnapshot()
    {
        lock (_receivedLock)
        {
            return Received.Select(message => (JsonObject)message.DeepClone()).ToList();
        }
    }

    private async Task HandleConnectionAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        lock (_headersLock)
        {
            _headers = context.Request.Headers.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        _connections.TryAdd(socket, 0);
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var message = await ReceiveJsonAsync(socket, context.RequestAborted);
                if (message is null)
                {
                    return;
                }

                await HandleEventAsync(socket, message, context.RequestAborted);
            }
        }
        catch (WebSocketException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _connections.TryRemove(socket, out _);
        }
    }

    private async Task HandleEventAsync(WebSocket socket, JsonObject message, CancellationToken cancellationToken)
    {
        var type = message["type"]?.GetValue<string>() ?? string.Empty;
        if (type == "session.input_audio.append")
        {
            var audio = message["audio"]?.GetValue<string>() ?? string.Empty;
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(audio);
            }
            catch (FormatException)
            {
                bytes = [];
            }

            var append = new JsonObject
            {
                ["type"] = type,
                ["audioLength"] = audio.Length,
                ["silent"] = bytes.All(value => value == 0)
            };
            if (KeepAudio) append["audio"] = audio;
            Record(append);
            if (!_inputMuted) Interlocked.Add(ref _inputMs, bytes.Length / 48);
            if (bytes.Length > 960)
            {
                _burstSeen = true;
                _burstAppends++;
                if (GateReadsAfterBurstAppends is { } gateAfter && _burstAppends >= gateAfter)
                {
                    await ReadGate.Task.WaitAsync(cancellationToken);
                }
            }
            else if (_burstSeen && !_answerStarted && PacedAnswerFrames > 0)
            {
                // The first ordinary 960-byte append after a burst: the burst is over, so the answer begins.
                _answerStarted = true;
                _ = SendPacedAnswerAsync(socket, Interlocked.Read(ref _inputMs), cancellationToken);
            }

            return;
        }

        Record((JsonObject)message.DeepClone());
        switch (type)
        {
            case "session.start":
                Interlocked.Increment(ref _startCount);
                if (StartGate is { } startGate)
                {
                    await startGate.Task.WaitAsync(cancellationToken);
                }

                if (StartDelayMs > 0)
                {
                    await Task.Delay(StartDelayMs, cancellationToken);
                }

                var session = message["session"] as JsonObject;
                if (session?["model"]?.GetValue<string>() == "bad-model")
                {
                    await SendAsync(socket, new JsonObject
                    {
                        ["type"] = "error",
                        ["error"] = new JsonObject
                        {
                            ["type"] = "invalid_request_error",
                            ["code"] = "invalid_model",
                            ["message"] = "unknown model",
                            ["client_event_id"] = message["event_id"]?.GetValue<string>()
                        }
                    }, cancellationToken);
                    return;
                }

                if (DelegationStartRejections > 0 && session?["delegation"] is not null)
                {
                    DelegationStartRejections--;
                    await SendAsync(socket, new JsonObject
                    {
                        ["type"] = "error",
                        ["error"] = new JsonObject
                        {
                            ["type"] = "invalid_request_error",
                            ["code"] = "delegation_unavailable",
                            ["message"] = "delegation backend is unavailable",
                            ["param"] = "session.delegation.responses.model",
                            ["client_event_id"] = message["event_id"]?.GetValue<string>()
                        }
                    }, cancellationToken);
                    if (CloseAfterDelegationRejection)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "startup rejected", cancellationToken);
                    }

                    return;
                }

                if (ToolsStartRejections > 0 && session?["delegation"]?["responses"]?["tools"] is not null)
                {
                    ToolsStartRejections--;
                    await SendAsync(socket, new JsonObject
                    {
                        ["type"] = "error",
                        ["error"] = new JsonObject
                        {
                            ["type"] = "invalid_request_error",
                            ["code"] = "tools_not_supported",
                            ["message"] = "tools are not supported on this deployment",
                            ["param"] = "session.delegation.responses.tools",
                            ["client_event_id"] = message["event_id"]?.GetValue<string>()
                        }
                    }, cancellationToken);
                    if (CloseAfterDelegationRejection)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "startup rejected", cancellationToken);
                    }

                    return;
                }

                var started = (JsonObject)(session?.DeepClone() ?? new JsonObject());
                started["id"] = SessionId;
                started["expires_at"] = 4102444800L;
                await SendAsync(socket, new JsonObject { ["type"] = "session.started", ["session"] = started }, cancellationToken);
                return;
            case "session.instructions.append":
            case "session.thinking.append":
            case "session.commentary.append":
                var kind = type.Split('.')[1];
                await SendAsync(socket, new JsonObject
                {
                    ["type"] = $"session.{kind}.appended",
                    ["client_event_id"] = message["event_id"]?.GetValue<string>(),
                    ["start_ms"] = 0,
                    ["end_ms"] = 10
                }, cancellationToken);
                if (kind == "instructions")
                {
                    _ = SendInstructionAudioAsync(socket, message["event_id"]?.GetValue<string>(), cancellationToken);
                }

                return;
            case "session.input_audio.mute":
                _inputMuted = true;
                await SendAsync(socket, new JsonObject { ["type"] = "session.input_audio.muted" }, cancellationToken);
                return;
            case "session.input_audio.unmute":
                _inputMuted = false;
                // The upstream echoes the unmute's event id as client_event_id (T1 trace: "unmute-3").
                var unmuteId = message["event_id"]?.GetValue<string>();
                if (UnmuteAckGate is { } ackGate)
                {
                    _ = SendAfterAsync(ackGate.Task, socket, new JsonObject { ["type"] = "session.input_audio.unmuted", ["client_event_id"] = unmuteId }, cancellationToken);
                    return;
                }

                await SendAsync(socket, new JsonObject { ["type"] = "session.input_audio.unmuted", ["client_event_id"] = unmuteId }, cancellationToken);
                return;
            case "session.close":
                if (!IgnoreClose)
                {
                    await SendAsync(socket, new JsonObject
                    {
                        ["type"] = "session.usage.updated",
                        ["usage"] = new JsonObject { ["seconds"] = 7 },
                        ["context_window"] = new JsonObject { ["usage_ratio"] = 0.1 }
                    }, cancellationToken);
                    await SendAsync(socket, new JsonObject
                    {
                        ["type"] = "session.closed",
                        ["reason"] = "client_request",
                        ["usage"] = new JsonObject { ["seconds"] = 7 }
                    }, cancellationToken);
                    await Task.Delay(20, cancellationToken);
                    if (socket.State == WebSocketState.Open)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cancellationToken);
                    }
                }

                return;
        }
    }

    private static async Task SendAfterAsync(Task gate, WebSocket socket, JsonObject message, CancellationToken cancellationToken)
    {
        try
        {
            await gate.WaitAsync(cancellationToken);
            await SendAsync(socket, message, cancellationToken);
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
        }
    }

    private async Task SendPacedAnswerAsync(WebSocket socket, long startInputMs, CancellationToken cancellationToken)
    {
        try
        {
            for (var index = 0; index < PacedAnswerFrames; index++)
            {
                var needed = startInputMs + (index + 1) * 20L;
                var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
                while (Interlocked.Read(ref _inputMs) < needed)
                {
                    // The upstream stalls without input; give up rather than hang the test.
                    if (DateTimeOffset.UtcNow > deadline || socket.State != WebSocketState.Open) return;
                    await Task.Delay(5, cancellationToken);
                }

                await SendAsync(socket, new JsonObject
                {
                    ["type"] = "session.output_audio.delta",
                    ["delta"] = Convert.ToBase64String(AnswerDelta()),
                    ["start_ms"] = 60_000 + index * 20,
                    ["end_ms"] = 60_000 + index * 20 + 20
                }, cancellationToken);
                Interlocked.Increment(ref _answerFramesSent);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
        }
    }

    /// <summary>A voiced 20 ms answer frame distinguishable from narration audio (a square wave of ±2000).</summary>
    public static byte[] AnswerDelta()
    {
        var bytes = new byte[960];
        for (var index = 0; index < 480; index++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(index * 2, 2), (short)(index % 2 == 0 ? 2000 : -2000));
        }

        return bytes;
    }

    private async Task SendInstructionAudioAsync(WebSocket socket, string? eventId, CancellationToken cancellationToken)
    {
        for (var index = 0; index < AudioDeltasPerAppend; index++)
        {
            if (index > 0)
            {
                await Task.Delay(DeltaGapMs, cancellationToken);
            }

            if (socket.State != WebSocketState.Open)
            {
                return;
            }

            var start = index * 20;
            await SendAsync(socket, new JsonObject
            {
                ["type"] = "session.output_audio.delta",
                ["delta"] = Convert.ToBase64String(VoicedDelta()),
                ["start_ms"] = start,
                ["end_ms"] = start + 20
            }, cancellationToken);
            if (index == 0)
            {
                await SendAsync(socket, new JsonObject
                {
                    ["type"] = "session.output_transcript.delta",
                    ["delta"] = $"speaking {eventId}",
                    ["start_ms"] = start,
                    ["end_ms"] = start + 20
                }, cancellationToken);
            }
        }
    }

    private void Record(JsonObject message)
    {
        lock (_receivedLock)
        {
            Received.Add(message);
        }
    }

    private static async Task<JsonObject?> ReceiveJsonAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return result.MessageType == WebSocketMessageType.Text
            ? JsonNode.Parse(stream.ToArray()) as JsonObject
            : null;
    }

    private static async Task SendAsync(WebSocket socket, JsonObject message, CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }

    private static byte[] VoicedDelta()
    {
        var bytes = new byte[960];
        for (var index = 0; index < 480; index++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(index * 2, 2), (short)Math.Round(Math.Sin(index / 5d) * 3000));
        }

        return bytes;
    }
}
