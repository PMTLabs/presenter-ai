using System.Net.WebSockets;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Api.Realtime;
using PresenterAi.Api.Tests.Infrastructure;
using PresenterAi.Infrastructure.Tests.Live;

namespace PresenterAi.Api.Tests;

public sealed class BridgeSlotTests
{
    [Fact]
    public async Task Take_over_when_the_holders_writer_has_failed_still_releases()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        using var first = await BridgeTestSupport.ConnectAsync(factory);
        await factory.Services.GetRequiredService<PresenterBridge>().StopCurrentWriterForTestAsync();

        using var second = await BridgeTestSupport.ConnectWithTicketAsync(factory, takeOver: true);
        (await BridgeTestSupport.ReceiveUntilAsync(second, frame => frame["type"]?.GetValue<string>() == "state"))["state"]!.GetValue<string>().Should().Be("idle");
    }

    [Fact]
    public async Task Take_over_that_waits_past_the_bound_gets_busy()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        factory.Services.GetRequiredService<PresenterBridge>().ConfigureTakeOverBoundForTest(TimeSpan.FromMilliseconds(100));
        var recorders = factory.Services.GetRequiredService<TestSessionRecorderFactory>();
        recorders.GateEnd = true;
        using var first = await BridgeTestSupport.ConnectAsync(factory);
        await BridgeTestSupport.SendAsync(first, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        _ = await BridgeTestSupport.ReceiveUntilAsync(first, frame => frame["type"]?.GetValue<string>() == "state" && frame["state"]?.GetValue<string>() == "presenting");

        using var second = await BridgeTestSupport.ConnectWithTicketAsync(factory, takeOver: true);
        var busy = await BridgeTestSupport.ReceiveUntilAsync(second, frame => frame["code"]?.GetValue<string>() == "busy");
        busy["canTakeOver"]!.GetValue<bool>().Should().BeTrue();
        recorders.EndGate.TrySetResult();
    }

    [Fact]
    public async Task Disconnect_of_a_superseded_client_does_not_end_the_new_clients_session()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        using var first = await BridgeTestSupport.ConnectAsync(factory);
        // Let the connect handshake finish enqueueing (trainer_state follows state) before the writer is stopped; a
        // handshake frame enqueued after the stop would be a genuine backpressure failure, not the modelled fault.
        _ = await BridgeTestSupport.ReceiveUntilAsync(first, frame => frame["type"]?.GetValue<string>() == "trainer_state");
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
