using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Api.Tests.Infrastructure;
using PresenterAi.Application.Auth;
using PresenterAi.Infrastructure.Tests.Live;

namespace PresenterAi.Api.Tests;

internal static class BridgeTestSupport
{
    public static ApiFactory Factory(FakeLiveServer fake, string model = "gpt-live-1", bool queuedPresenter = false) => new()
    {
        UseQueuedPresenter = queuedPresenter,
        Overrides = new Dictionary<string, string?>
        {
            ["Upstream:Endpoint"] = fake.Url,
            ["Upstream:Model"] = model,
            ["Presenter:AdvanceSilenceMs"] = "200"
        }
    };

    public static async Task<WebSocket> ConnectAsync(ApiFactory factory)
    {
        var socket = await ConnectWithTicketAsync(factory);
        _ = await ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "state");
        return socket;
    }

    public static async Task<WebSocket> ConnectWithTicketAsync(ApiFactory factory, string userId = "test-user", bool takeOver = false)
    {
        var ticket = Guid.NewGuid().ToString("N");
        await factory.Services.GetRequiredService<ITicketStore>().IssueAsync(ticket, userId);
        var socket = await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
        await SendAsync(socket, $"{{\"type\":\"auth\",\"ticket\":\"{ticket}\"{(takeOver ? ",\"takeOver\":true" : string.Empty)}}}");
        return socket;
    }

    // The previous owner releases the slot only after its cleanup, so a connect made straight after a disconnect
    // can legitimately be told busy; retry until the slot is free.
    public static async Task<WebSocket> ConnectWhenFreeAsync(ApiFactory factory)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (true)
        {
            var socket = await ConnectWithTicketAsync(factory);
            var frame = await ReceiveAsync(socket);
            if (frame.Text is not null && !frame.Text.Contains("\"code\":\"busy\"", StringComparison.Ordinal)) return socket;
            socket.Dispose();
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Timed out waiting for the bridge slot.");
            await Task.Delay(20);
        }
    }

    public static async Task<WebSocket> ConnectAnonymousAsync(ApiFactory factory) =>
        await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);

    public static async Task SendAsync(WebSocket socket, string text) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    public static async Task SendFragmentedAsync(WebSocket socket, string text, WebSocketMessageType messageType) =>
        await SendFragmentedAsync(socket, Encoding.UTF8.GetBytes(text), messageType).ConfigureAwait(false);

    public static async Task SendFragmentedAsync(WebSocket socket, byte[] bytes, WebSocketMessageType messageType)
    {
        var split = bytes.Length / 2;
        await socket.SendAsync(bytes.AsMemory(0, split), messageType, false, CancellationToken.None).ConfigureAwait(false);
        await socket.SendAsync(bytes.AsMemory(split), messageType, true, CancellationToken.None).ConfigureAwait(false);
    }

    public static async Task<JsonObject> ReceiveUntilAsync(WebSocket socket, Func<JsonObject, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var frame = await ReceiveAsync(socket);
            if (frame.Text is not null)
            {
                var json = JsonNode.Parse(frame.Text)!.AsObject();
                if (predicate(json)) return json;
            }
        }

        throw new TimeoutException("Timed out waiting for bridge frame.");
    }

    public static async Task<byte[]> ReceiveBinaryAsync(WebSocket socket)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var frame = await ReceiveAsync(socket);
            if (frame.Binary is not null) return frame.Binary;
        }

        throw new TimeoutException("Timed out waiting for binary bridge frame.");
    }

    public static async Task<(string? Text, byte[]? Binary)> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[32 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            if (result.MessageType == WebSocketMessageType.Close) return (null, null);
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return result.MessageType == WebSocketMessageType.Binary
            ? (null, stream.ToArray())
            : (Encoding.UTF8.GetString(stream.ToArray()), null);
    }

    public static async Task<WebSocketReceiveResult> ReceiveCloseAsync(WebSocket socket)
    {
        var result = await socket.ReceiveAsync(new byte[1024], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        return result;
    }

    /// <summary>Skips data frames (the connect handshake's trailing frames, say) and returns the close frame.</summary>
    public static async Task<WebSocketReceiveResult> ReceiveUntilCloseAsync(WebSocket socket)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await socket.ReceiveAsync(new byte[32 * 1024], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            if (result.MessageType == WebSocketMessageType.Close) return result;
        }

        throw new TimeoutException("Timed out waiting for the close frame.");
    }

    public static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for observable fake-server state.");
    }
}
