using System.Net.WebSockets;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Api.Tests.Infrastructure;
using PresenterAi.Application.Presenting;
using PresenterAi.Infrastructure.Tests.Live;

namespace PresenterAi.Api.Tests;

public sealed class BridgeSessionRecorderTests
{
    [Fact]
    public async Task Disconnect_waits_for_the_recorder_barrier_before_releasing_the_slot()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        var recorders = factory.Services.GetRequiredService<TestSessionRecorderFactory>();
        recorders.GateEnd = true;
        using var first = await BridgeTestSupport.ConnectAsync(factory);
        await BridgeTestSupport.SendAsync(first, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        await BridgeTestSupport.ReceiveUntilAsync(first, frame => frame["type"]?.GetValue<string>() == "state" && frame["state"]?.GetValue<string>() == "presenting");

        await first.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "disconnect", CancellationToken.None);
        await BridgeTestSupport.WaitForAsync(() => recorders.Recorders.Count == 1 && recorders.Recorders[0].EndCount == 1);

        using var blocked = await BridgeTestSupport.ConnectWithTicketAsync(factory);
        var busy = await BridgeTestSupport.ReceiveAsync(blocked);
        busy.Text.Should().Contain("\"code\":\"busy\"");

        recorders.EndGate.TrySetResult();
        using var next = await BridgeTestSupport.ConnectWhenFreeAsync(factory);
        next.State.Should().Be(WebSocketState.Open);
    }

    [Fact]
    public async Task Two_sequential_runs_do_not_leak_handlers()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        var recorders = factory.Services.GetRequiredService<TestSessionRecorderFactory>();

        using (var first = await BridgeTestSupport.ConnectAsync(factory))
        {
            await BridgeTestSupport.SendAsync(first, "{\"type\":\"start\",\"presentation\":\"sample\"}");
            await BridgeTestSupport.ReceiveUntilAsync(first, frame => frame["type"]?.GetValue<string>() == "state" && frame["state"]?.GetValue<string>() == "presenting");
            await first.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "first run", CancellationToken.None);
            await BridgeTestSupport.WaitForAsync(() => recorders.Recorders.Count == 1 && recorders.Recorders[0].DetachCount == 1);
        }

        using (var second = await BridgeTestSupport.ConnectWhenFreeAsync(factory))
        {
            await BridgeTestSupport.SendAsync(second, "{\"type\":\"start\",\"presentation\":\"sample\"}");
            await BridgeTestSupport.ReceiveUntilAsync(second, frame => frame["type"]?.GetValue<string>() == "state" && frame["state"]?.GetValue<string>() == "presenting");
            await second.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "second run", CancellationToken.None);
        }

        await BridgeTestSupport.WaitForAsync(() => recorders.Recorders.Count == 2 && recorders.Recorders[1].DetachCount == 1);
        recorders.Recorders.Should().HaveCount(2);
        recorders.Recorders.Should().OnlyContain(recorder => recorder.AttachCount == 1 && recorder.DetachCount == 1);
    }

    [Fact]
    public async Task Disconnect_holds_the_slot_until_the_presenter_is_idle_so_the_next_run_is_recorded()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        var recorders = factory.Services.GetRequiredService<TestSessionRecorderFactory>();
        var presenter = factory.Services.GetRequiredService<IPresenter>();
        using var inClosed = new ManualResetEventSlim();
        using var releaseClosed = new ManualResetEventSlim();
        // Presenter.OnClosed raises Closed before it sets idle, so blocking here holds the presenter in "ending".
        // The hold also lets go as soon as the bridge reaches the recorder barrier, which it must not do yet.
        presenter.Closed += _ =>
        {
            inClosed.Set();
            SpinWait.SpinUntil(() => releaseClosed.IsSet || recorders.Recorders[0].EndCount > 0, TimeSpan.FromSeconds(5));
        };

        using (var first = await BridgeTestSupport.ConnectAsync(factory))
        {
            await BridgeTestSupport.SendAsync(first, "{\"type\":\"start\",\"presentation\":\"sample\"}");
            await BridgeTestSupport.ReceiveUntilAsync(first, frame => frame["type"]?.GetValue<string>() == "state" && frame["state"]?.GetValue<string>() == "presenting");
            await first.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "disconnect", CancellationToken.None);
            inClosed.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

            using var blocked = await BridgeTestSupport.ConnectWithTicketAsync(factory);
            (await BridgeTestSupport.ReceiveAsync(blocked)).Text.Should().Contain("\"code\":\"busy\"");
            releaseClosed.Set();
            await BridgeTestSupport.WaitForAsync(() => recorders.Recorders[0].DetachCount == 1);
        }

        recorders.Recorders[0].ClosedBeforeEnd.Should().BeTrue();
        using var second = await BridgeTestSupport.ConnectWhenFreeAsync(factory);
        await BridgeTestSupport.SendAsync(second, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        await BridgeTestSupport.ReceiveUntilAsync(second, frame => frame["type"]?.GetValue<string>() == "state" && frame["state"]?.GetValue<string>() == "presenting");
        await BridgeTestSupport.WaitForAsync(() => recorders.Recorders.Count == 2 && recorders.Recorders[1].BeginCount == 1);
    }

    [Fact]
    public async Task Start_while_presenting_keeps_the_first_recorder()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        var recorders = factory.Services.GetRequiredService<TestSessionRecorderFactory>();
        using var socket = await BridgeTestSupport.ConnectAsync(factory);

        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "state" && frame["state"]?.GetValue<string>() == "presenting");
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        await BridgeTestSupport.ReceiveUntilAsync(socket, frame =>
            frame["type"]?.GetValue<string>() == "log"
            && frame["message"]?.GetValue<string>()?.Contains("start ignored", StringComparison.Ordinal) == true);

        recorders.Recorders.Should().HaveCount(1);
        recorders.Recorders[0].EndCount.Should().Be(0);
        recorders.Recorders[0].DetachCount.Should().Be(0);
    }
}
