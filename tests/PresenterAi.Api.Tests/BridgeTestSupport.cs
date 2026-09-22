using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PresenterAi.Api.Tests.Infrastructure;
using PresenterAi.Infrastructure.Tests.Live;

namespace PresenterAi.Api.Tests;

internal static class BridgeTestSupport
{
    public static ApiFactory Factory(FakeLiveServer fake, string model = "gpt-live-1") => new()
    {
        Overrides = new Dictionary<string, string?>
        {
            ["Upstream:Endpoint"] = fake.Url,
            ["Upstream:Model"] = model,
            ["Presenter:AdvanceSilenceMs"] = "200"
        }
    };

    public static Microsoft.AspNetCore.TestHost.WebSocketClient AuthenticatedWebSocketClient(ApiFactory factory)
    {
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Authorization =
            $"Bearer {ApiFactory.CreateTestToken("test-user", "test@presenter-ai.local")}";
        return client;
    }

    public static async Task<WebSocket> ConnectAsync(ApiFactory factory)
    {
        var socket = await AuthenticatedWebSocketClient(factory).ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
        _ = await ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "state");
        return socket;
    }

    public static async Task SendAsync(WebSocket socket, string text) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

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
