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

    public bool IgnoreClose { get; set; }

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

    public static async Task<FakeLiveServer> StartAsync(int audioDeltasPerAppend = 3, int deltaGapMs = 20)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var server = new FakeLiveServer(app, 0)
        {
            AudioDeltasPerAppend = audioDeltasPerAppend,
            DeltaGapMs = deltaGapMs
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

            Record(new JsonObject
            {
                ["type"] = type,
                ["audioLength"] = audio.Length,
                ["silent"] = bytes.All(value => value == 0)
            });
            return;
        }

        Record((JsonObject)message.DeepClone());
        switch (type)
        {
            case "session.start":
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

                var started = (JsonObject)(session?.DeepClone() ?? new JsonObject());
                started["id"] = "sess_fake";
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
                await SendAsync(socket, new JsonObject { ["type"] = "session.input_audio.muted" }, cancellationToken);
                return;
            case "session.input_audio.unmute":
                await SendAsync(socket, new JsonObject { ["type"] = "session.input_audio.unmuted" }, cancellationToken);
                return;
            case "session.close":
                if (!IgnoreClose)
                {
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
