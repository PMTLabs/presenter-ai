using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Api.Realtime;
using PresenterAi.Infrastructure.Tests.Live;

namespace PresenterAi.Api.Tests;

public sealed class BridgeTests
{
    [Fact]
    public async Task Start_presents_slide_1_and_auto_advances()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        var presenting = await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "state" && frame["state"]?.GetValue<string>() == "presenting");
        presenting["slideCount"]!.GetValue<int>().Should().Be(3);
        (await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "slide"))["index"]!.GetValue<int>().Should().Be(0);
        (await BridgeTestSupport.ReceiveBinaryAsync(socket)).Should().HaveCount(960);
        (await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "slide" && frame["index"]?.GetValue<int>() == 1))["index"]!.GetValue<int>().Should().Be(1);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"next\"}");
        _ = await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "slide" && frame["index"]?.GetValue<int>() == 2);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"goto\",\"index\":0}");
        _ = await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "slide" && frame["index"]?.GetValue<int>() == 0);
        var before = fake.ReceivedSnapshot().Count(message => message["type"]?.GetValue<string>() == "session.input_audio.append" && message["silent"]?.GetValue<bool>() == false);
        await socket.SendAsync(new byte[960].Select(_ => (byte)1).ToArray(), WebSocketMessageType.Binary, true, CancellationToken.None);
        await socket.SendAsync(new byte[961].Select(_ => (byte)1).ToArray(), WebSocketMessageType.Binary, true, CancellationToken.None);
        await BridgeTestSupport.WaitForAsync(() => fake.ReceivedSnapshot().Count(message => message["type"]?.GetValue<string>() == "session.input_audio.append" && message["silent"]?.GetValue<bool>() == false) >= before + 2);
        var appends = fake.ReceivedSnapshot().Where(message => message["type"]?.GetValue<string>() == "session.input_audio.append" && message["silent"]?.GetValue<bool>() == false).TakeLast(2).ToArray();
        appends.Select(message => message["audioLength"]!.GetValue<int>()).Should().OnlyContain(length => length == Convert.ToBase64String(new byte[960]).Length);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"pause\"}");
        await BridgeTestSupport.WaitForAsync(() => fake.ReceivedSnapshot().Any(message => message["type"]?.GetValue<string>() == "session.input_audio.mute"));
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"end\"}");
        (await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "closed"))["seconds"]!.GetValue<double>().Should().Be(7);
        fake.ReceivedSnapshot().Any(message => message["type"]?.GetValue<string>() == "session.close").Should().BeTrue();
    }

    [Fact]
    public async Task Second_client_is_refused_with_busy_and_1013()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        using var first = await BridgeTestSupport.ConnectAsync(factory);
        using var second = await BridgeTestSupport.ConnectWithTicketAsync(factory);
        var error = (await BridgeTestSupport.ReceiveAsync(second)).Text!;
        JsonNode.Parse(error)!["code"]!.GetValue<string>().Should().Be("busy");
        var close = await second.ReceiveAsync(new byte[32], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        close.MessageType.Should().Be(WebSocketMessageType.Close);
        close.CloseStatus.Should().Be((WebSocketCloseStatus)1013);
    }

    [Fact]
    public async Task Browser_disconnect_ends_live_session()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        _ = await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "state" && frame["state"]?.GetValue<string>() == "presenting");
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await BridgeTestSupport.WaitForAsync(() => fake.ReceivedSnapshot().Count(message => message["type"]?.GetValue<string>() == "session.close") == 1);
    }

    [Fact]
    public async Task Upstream_startup_error_is_reported_and_presenter_returns_idle()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake, "bad-model");
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        var error = await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "error");
        error["code"]!.GetValue<string>().Should().Be("invalid_model");
        _ = await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "state" && frame["state"]?.GetValue<string>() == "idle");
    }

    [Fact]
    public async Task Simultaneous_clients_exactly_one_wins()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        var pair = await Task.WhenAll(BridgeTestSupport.ConnectWithTicketAsync(factory), BridgeTestSupport.ConnectWithTicketAsync(factory));
        using var a = pair[0]; using var b = pair[1];
        var first = await BridgeTestSupport.ReceiveAsync(a); var second = await BridgeTestSupport.ReceiveAsync(b);
        new[] { first.Text, second.Text }.Count(text => text is not null && JsonNode.Parse(text)!["type"]?.GetValue<string>() == "state").Should().Be(1);
        new[] { first.Text, second.Text }.Any(text => text is not null && JsonNode.Parse(text)!["code"]?.GetValue<string>() == "busy").Should().BeTrue();
    }

    [Fact]
    public async Task Client_close_during_send_ends_once()
    {
        await using var fake = await FakeLiveServer.StartAsync(audioDeltasPerAppend: 50);
        using var factory = BridgeTestSupport.Factory(fake);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        _ = await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "state" && frame["state"]?.GetValue<string>() == "presenting");
        _ = await BridgeTestSupport.ReceiveBinaryAsync(socket);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "close during audio", CancellationToken.None);
        await BridgeTestSupport.WaitForAsync(() => fake.ReceivedSnapshot().Count(message => message["type"]?.GetValue<string>() == "session.close") == 1);
    }

    [Fact]
    public async Task Ping_is_answered_while_start_is_connecting()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        fake.StartDelayMs = 1000;
        using var factory = BridgeTestSupport.Factory(fake);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ping\"}");
        var pong = await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "pong");
        pong["type"]!.GetValue<string>().Should().Be("pong");
    }

    [Fact]
    public async Task Client_that_cannot_drain_fills_outbound_queue_and_is_closed_1011()
    {
        await using var fake = await FakeLiveServer.StartAsync(audioDeltasPerAppend: 50);
        using var factory = BridgeTestSupport.Factory(fake);
        var sendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        factory.Services.GetRequiredService<PresenterBridge>().ConfigureOutboundForTest(
            2,
            () => Interlocked.Increment(ref sends) == 1 ? Task.CompletedTask : sendGate.Task);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);

        // TestServer buffers socket writes, so block the real writer before SendAsync. The live server then emits
        // enough audio/transcript frames to make ClientConnection.Enqueue observe TryWrite == false.
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        await BridgeTestSupport.WaitForAsync(() => fake.ReceivedSnapshot().Any(message => message["type"]?.GetValue<string>() == "session.instructions.append"));
        sendGate.TrySetResult();

        WebSocketReceiveResult close;
        do
        {
            close = await socket.ReceiveAsync(new byte[32 * 1024], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        while (close.MessageType != WebSocketMessageType.Close);
        close.CloseStatus.Should().Be((WebSocketCloseStatus)1011);
    }
}
