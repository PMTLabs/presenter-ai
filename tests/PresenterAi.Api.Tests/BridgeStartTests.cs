using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using PresenterAi.Api.Tests.Infrastructure;
using PresenterAi.Infrastructure.Tests.Live;

namespace PresenterAi.Api.Tests;

public sealed class BridgeStartTests
{
    [Fact]
    public async Task Old_client_start_with_presentation_field_starts_that_deck()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = new ApiFactory
        {
            Overrides = new Dictionary<string, string?>
            {
                ["Upstream:Endpoint"] = fake.Url,
                ["Presenter:AdvanceSilenceMs"] = "200"
            }
        };
        var client = BridgeTestSupport.AuthenticatedWebSocketClient(factory);
        using var socket = await client.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
        _ = await ReceiveTextAsync(socket);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"start\",\"presentation\":\"ricoh-delivery-overview\",\"fromIndex\":0}"), WebSocketMessageType.Text, true, CancellationToken.None);
        await WaitUntilAsync(() => fake.ReceivedSnapshot().Any(message =>
            message["type"]?.GetValue<string>() == "session.instructions.append"
            && message["content"]?.GetValue<string>().Contains("Good morning everyone", StringComparison.Ordinal) == true));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        condition().Should().BeTrue("the fake upstream should receive the selected deck's narration");
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket)
    {
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }
}
