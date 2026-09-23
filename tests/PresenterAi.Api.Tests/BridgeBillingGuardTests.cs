using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PresenterAi.Api.Realtime;
using PresenterAi.Api.Tests.Infrastructure;
using PresenterAi.Application.Presenting;
using PresenterAi.Infrastructure.Tests.Live;

namespace PresenterAi.Api.Tests;

public sealed class BridgeBillingGuardTests
{
    [Fact]
    public async Task Silent_browser_is_aborted_at_45_s_ends_the_talk_and_frees_the_slot()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        factory.UseFakeClock();
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        var presenter = factory.Services.GetRequiredService<IPresenter>();
        PresenterClosed? closed = null;
        presenter.Closed += value => closed = value;
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["state"]?.GetValue<string>() == "presenting");
        await BridgeTestSupport.WaitForAsync(() => fake.ConnectionCount == 1);
        await factory.AdvanceAndSettleAsync(TimeSpan.FromSeconds(45));
        await BridgeTestSupport.WaitForAsync(() => closed is not null &&
            factory.Services.GetRequiredService<TestSessionRecorderFactory>().Recorders.Single().EndCount == 1);
        closed!.EndReason.Should().Be(EndReasons.Heartbeat);
        var recorder = factory.Services.GetRequiredService<TestSessionRecorderFactory>().Recorders.Single();
        recorder.LastClosed!.EndReason.Should().Be(EndReasons.Heartbeat);
        presenter.Snapshot().State.Should().Be("idle");
        fake.ConnectionCount.Should().Be(0);
        using var next = await BridgeTestSupport.ConnectWhenFreeAsync(factory);
        presenter.Snapshot().State.Should().Be("idle");
        fake.ConnectionCount.Should().Be(0);
    }

    [Fact]
    public async Task Frames_keep_the_connection_alive()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        factory.UseFakeClock();
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await factory.AdvanceAndSettleAsync(TimeSpan.FromSeconds(30));
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"pong\"}");
        // A round-trip proves the receive loop processed pong before advancing the clock again.
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ping\"}");
        await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "pong");
        await factory.AdvanceAndSettleAsync(TimeSpan.FromSeconds(30));
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ping\"}");
        await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "pong");
    }

    [Theory]
    [InlineData("4")]
    [InlineData("5.5")]
    [InlineData("\"5\"")]
    public async Task Start_max_minutes_below_five_is_a_protocol_error(string minutes)
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await BridgeTestSupport.SendAsync(socket,
            $"{{\"type\":\"start\",\"presentation\":\"sample\",\"maxMinutes\":{minutes}}}");
        var error = await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "error");
        error["code"]!.GetValue<string>().Should().Be("protocol");
        fake.ConnectionCount.Should().Be(0);
    }

    [Fact]
    public async Task Start_max_minutes_override_lowers_the_cap_and_emits_warning()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        factory.UseFakeClock();
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        var presenter = factory.Services.GetRequiredService<IPresenter>();
        PresenterClosed? result = null;
        presenter.Closed += closed => result = closed;
        await BridgeTestSupport.SendAsync(socket,
            "{\"type\":\"start\",\"presentation\":\"sample\",\"maxMinutes\":5}");
        await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["state"]?.GetValue<string>() == "presenting");
        var frames = await KeepAliveForAsync(factory, socket, 8);
        var warning = frames.Single(frame => frame["type"]?.GetValue<string>() == "limit_warning"
            && frame["kind"]?.GetValue<string>() == EndReasons.MaxLength);
        warning["kind"]!.GetValue<string>().Should().Be(EndReasons.MaxLength);
        warning["secondsLeft"]!.GetValue<int>().Should().Be(60);
        await KeepAliveForAsync(factory, socket, 1);
        await factory.AdvanceAndSettleAsync(TimeSpan.FromSeconds(30));
        await BridgeTestSupport.WaitForAsync(() => result is not null);
        result!.EndReason.Should().Be(EndReasons.MaxLength);
        var closed = await BridgeTestSupport.ReceiveUntilAsync(socket,
            frame => frame["type"]?.GetValue<string>() == "closed");
        closed["endReason"]!.GetValue<string>().Should().Be(EndReasons.MaxLength);
    }

    [Fact]
    public async Task Pause_close_sends_upstream_suspended_and_resume_sends_live()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        factory.UseFakeClock();
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["state"]?.GetValue<string>() == "presenting");
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"pause\"}");
        await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["state"]?.GetValue<string>() == "paused");
        var frames = await KeepAliveForAsync(factory, socket, 4);
        var suspended = frames.Single(frame => frame["type"]?.GetValue<string>() == "upstream" &&
            frame["status"]?.GetValue<string>() == "suspended");
        suspended["status"]!.GetValue<string>().Should().Be("suspended");
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"resume\"}");
        var live = await BridgeTestSupport.ReceiveUntilAsync(socket,
            frame => frame["type"]?.GetValue<string>() == "upstream" &&
                frame["status"]?.GetValue<string>() == "live");
        live["status"]!.GetValue<string>().Should().Be("live");
    }

    [Fact]
    public async Task Writer_failure_ends_the_receive_loop_with_writer_failed()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        var fail = 0;
        factory.Services.GetRequiredService<PresenterBridge>().ConfigureOutboundForTest(500, () =>
        {
            if (Interlocked.CompareExchange(ref fail, 0, 1) == 1)
                throw new System.Net.WebSockets.WebSocketException("simulated writer failure");
            return Task.CompletedTask;
        });
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        var presenter = factory.Services.GetRequiredService<IPresenter>();
        PresenterClosed? closed = null;
        presenter.Closed += value => closed = value;
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["state"]?.GetValue<string>() == "presenting");
        Interlocked.Exchange(ref fail, 1);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ping\"}");
        await BridgeTestSupport.WaitForAsync(() => closed is not null);
        closed!.EndReason.Should().Be(EndReasons.WriterFailed);
        using var next = await BridgeTestSupport.ConnectWhenFreeAsync(factory);
        next.State.Should().Be(System.Net.WebSockets.WebSocketState.Open);
    }

    [Fact]
    public async Task Backpressure_abort_records_backpressure()
    {
        await using var fake = await FakeLiveServer.StartAsync(audioDeltasPerAppend: 50);
        using var factory = BridgeTestSupport.Factory(fake);
        var sendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        factory.Services.GetRequiredService<PresenterBridge>().ConfigureOutboundForTest(2,
            () => Interlocked.Increment(ref sends) == 1 ? Task.CompletedTask : sendGate.Task);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        var presenter = factory.Services.GetRequiredService<IPresenter>();
        PresenterClosed? closed = null;
        presenter.Closed += value => closed = value;
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        await BridgeTestSupport.WaitForAsync(() => fake.ReceivedSnapshot().Any(message =>
            message["type"]?.GetValue<string>() == "session.instructions.append"));
        sendGate.TrySetResult();
        await BridgeTestSupport.WaitForAsync(() => closed is not null);
        closed!.EndReason.Should().Be(EndReasons.Backpressure);
        factory.Services.GetRequiredService<TestSessionRecorderFactory>().Recorders.Single()
            .LastClosed!.EndReason.Should().Be(EndReasons.Backpressure);
    }

    [Fact]
    public async Task Start_observation_expiry_aborts_the_start_before_freeing_the_slot()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake, queuedPresenter: true);
        factory.UseFakeClock();
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        var queued = factory.Services.GetRequiredService<TestQueuedPresenter>();
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        await queued.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await socket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
            "disconnect", CancellationToken.None);
        await factory.AdvanceAndSettleAsync(TimeSpan.FromSeconds(90));
        using var contender = await BridgeTestSupport.ConnectWithTicketAsync(factory);
        var busy = await BridgeTestSupport.ReceiveUntilAsync(contender,
            frame => frame["code"]?.GetValue<string>() == "busy");
        busy["code"]!.GetValue<string>().Should().Be("busy");
        queued.StartGate.TrySetResult();
        using var next = await BridgeTestSupport.ConnectWhenFreeAsync(factory);
        queued.Snapshot().State.Should().Be("idle");
        fake.ConnectionCount.Should().Be(0);
    }

    [Fact]
    public async Task Application_stopping_closes_the_live_upstream()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        var presenter = factory.Services.GetRequiredService<IPresenter>();
        PresenterClosed? result = null;
        presenter.Closed += closed => result = closed;
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["state"]?.GetValue<string>() == "presenting");
        await factory.Services.GetServices<IHostedService>().OfType<PresenterShutdownService>().Single().StopAsync(CancellationToken.None);
        await BridgeTestSupport.WaitForAsync(() => result is not null && fake.ConnectionCount == 0);
        result!.EndReason.Should().Be(EndReasons.Shutdown);
        presenter.Snapshot().State.Should().Be("idle");
    }

    private static async Task<List<System.Text.Json.Nodes.JsonObject>> KeepAliveForAsync(
        ApiFactory factory, System.Net.WebSockets.WebSocket socket, int halfMinutes)
    {
        var frames = new List<System.Text.Json.Nodes.JsonObject>();
        for (var index = 0; index < halfMinutes; index++)
        {
            await factory.AdvanceAndSettleAsync(TimeSpan.FromSeconds(30));
            await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ping\"}");
            while (true)
            {
                var received = await BridgeTestSupport.ReceiveAsync(socket);
                if (received.Text is null) continue;
                var frame = System.Text.Json.Nodes.JsonNode.Parse(received.Text)!.AsObject();
                frames.Add(frame);
                if (frame["type"]?.GetValue<string>() == "pong") break;
            }
        }
        return frames;
    }

    [Fact]
    public async Task Closed_frame_carries_end_reason()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["state"]?.GetValue<string>() == "presenting");
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"end\"}");
        var closed = await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "closed");
        closed["endReason"]!.GetValue<string>().Should().Be(EndReasons.User);
        closed.ContainsKey("usageConfirmed").Should().BeTrue();
        closed.ContainsKey("estimatedSeconds").Should().BeTrue();
    }
}
