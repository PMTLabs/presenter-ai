using System.Net.WebSockets;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Api.Realtime;
using PresenterAi.Infrastructure.Tests.Live;

namespace PresenterAi.Api.Tests;

public sealed class BridgeSlotTests
{
    [Fact]
    public async Task Disconnect_of_a_superseded_client_does_not_end_the_new_clients_session()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        using var first = await BridgeTestSupport.ConnectAsync(factory);
        // This test seam models a writer failure while the owner's receive loop remains alive. Before the CAS fix,
        // writer completion released the slot and a second socket would incorrectly receive state.
        await factory.Services.GetRequiredService<PresenterBridge>().StopCurrentWriterForTestAsync();
        using var second = await BridgeTestSupport.ConnectWithTicketAsync(factory);
        var busy = (await BridgeTestSupport.ReceiveAsync(second)).Text;
        busy.Should().NotBeNull();
        JsonNode.Parse(busy!)!["code"]!.GetValue<string>().Should().Be("busy");
        await first.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "owner close", CancellationToken.None);
    }
}
