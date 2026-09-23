using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Api.Tests.Infrastructure;
using PresenterAi.Application.Auth;
using PresenterAi.Application.Tools.External;
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
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"start\",\"presentation\":\"ricoh-delivery-overview\",\"fromIndex\":0}"), WebSocketMessageType.Text, true, CancellationToken.None);
        await WaitUntilAsync(() => fake.ReceivedSnapshot().Any(message =>
            message["type"]?.GetValue<string>() == "session.instructions.append"
            && message["content"]?.GetValue<string>().Contains("Good morning everyone", StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task Socket_owner_not_frame_fields_is_passed_to_external_source()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        var source = new CapturingSource();
        using var factory = BridgeTestSupport.Factory(fake);
        factory.SessionToolSource = source;
        factory.Overrides = new Dictionary<string, string?>
        {
            ["Upstream:Endpoint"] = fake.Url,
            ["Upstream:DelegationModel"] = "managed",
            ["Presenter:AdvanceSilenceMs"] = "200"
        };
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"start\",\"presentation\":\"sample\",\"ownerId\":\"other\",\"userId\":\"other\",\"owner\":\"other\"}");
        await BridgeTestSupport.WaitForAsync(() => source.Owners.Count > 0);
        source.Owners.Should().Equal("test-user");
    }

    [Fact]
    public async Task Client_only_route_does_not_load_external_source()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        var source = new CapturingSource();
        using var factory = new ApiFactory
        {
            SessionToolSource = source,
            Overrides = new Dictionary<string, string?> { ["Upstream:Endpoint"] = fake.Url, ["Upstream:DelegationModel"] = "", ["Upstream:Fallback:DelegationModel"] = "", ["Presenter:AdvanceSilenceMs"] = "200" }
        };
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        _ = await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["state"]?.ToString() == "presenting");
        source.Owners.Should().BeEmpty();
    }

    [Fact]
    public async Task External_tool_page_log_omits_arguments_and_result()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        var source = new CapturingSource { Tools = [new LogTool()] };
        using var factory = ToolFactory(fake, source);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        _ = await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["state"]?.ToString() == "presenting");
        await fake.SendFunctionCallAsync("d", "call1", "log_tool", "{\"secret\":\"private-argument\"}");
        var toolLog = await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.ToString() == "log" && frame["message"]?.ToString().Contains("tool:") == true);
        toolLog.ToJsonString().Should().NotContain("private-argument").And.NotContain("private-result");
    }

    private sealed class LogTool : PresenterAi.Application.Tools.ITool
    {
        public string Name => "log_tool";
        public string Description => "Log tool";
        public JsonObject Parameters => new() { ["type"] = "object", ["properties"] = new JsonObject { ["secret"] = new JsonObject { ["type"] = "string" } } };
        public IReadOnlyList<string> Tags => [];
        public bool Pinned => false;
        public string Source => "fake";
        public Task<PresenterAi.Application.Tools.ToolResult> InvokeAsync(JsonElement args, CancellationToken ct = default) =>
            Task.FromResult(PresenterAi.Application.Tools.ToolResult.Success("private-result"));
    }

    [Fact]
    public async Task Same_user_takeover_disposes_old_set_and_reloads_only_own_tools()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        var source = new CapturingSource();
        using var factory = ToolFactory(fake, source);
        using var first = await BridgeTestSupport.ConnectAsync(factory);
        await BridgeTestSupport.SendAsync(first, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        _ = await BridgeTestSupport.ReceiveUntilAsync(first, frame => frame["state"]?.ToString() == "presenting");
        using var second = await BridgeTestSupport.ConnectWithTicketAsync(factory, takeOver: true);
        _ = await BridgeTestSupport.ReceiveUntilAsync(second, frame => frame["type"]?.ToString() == "state");
        await BridgeTestSupport.SendAsync(second, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        await BridgeTestSupport.WaitForAsync(() => source.Owners.Count == 2 && source.Sets[0].Count == 1);
        source.Owners.Should().Equal("test-user", "test-user");
    }

    [Fact]
    public async Task Different_user_busy_cannot_load_or_see_holder_and_loads_only_after_release()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        var source = new CapturingSource();
        using var factory = ToolFactory(fake, source);
        using var first = await BridgeTestSupport.ConnectAsync(factory);
        await BridgeTestSupport.SendAsync(first, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        _ = await BridgeTestSupport.ReceiveUntilAsync(first, frame => frame["state"]?.ToString() == "presenting");
        using var other = await BridgeTestSupport.ConnectWithTicketAsync(factory, "user-b", takeOver: true);
        var busy = await BridgeTestSupport.ReceiveUntilAsync(other, frame => frame["code"]?.ToString() == "busy");
        busy["canTakeOver"]!.GetValue<bool>().Should().BeFalse();
        busy.ToJsonString().Should().NotContain("test-user").And.NotContain("sample");
        source.Owners.Should().Equal("test-user");
        await BridgeTestSupport.SendAsync(first, "{\"type\":\"end\"}");
        _ = await BridgeTestSupport.ReceiveUntilAsync(first, frame => frame["state"]?.ToString() == "idle");
        await first.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await Task.Delay(100);
        using var b = await BridgeTestSupport.ConnectWithTicketAsync(factory, "user-b", takeOver: true);
        _ = await BridgeTestSupport.ReceiveUntilAsync(b, frame => frame["type"]?.ToString() == "state");
        await BridgeTestSupport.SendAsync(b, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        await BridgeTestSupport.WaitForAsync(() => source.Owners.Count == 2);
        source.Owners.Should().Equal("test-user", "user-b");
    }

    private static ApiFactory ToolFactory(FakeLiveServer fake, CapturingSource source) => new()
    {
        SessionToolSource = source,
        Overrides = new Dictionary<string, string?> { ["Upstream:Endpoint"] = fake.Url, ["Upstream:DelegationModel"] = "managed", ["Presenter:AdvanceSilenceMs"] = "200" }
    };

    private sealed class CapturingSource : ISessionToolSource
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Owners { get; } = new();
        public List<CountingDisposable> Sets { get; } = [];
        public IReadOnlyList<PresenterAi.Application.Tools.ITool> Tools { get; set; } = [];
        public Task<SessionToolSet> LoadAsync(string ownerId, CancellationToken ct = default)
        {
            Owners.Enqueue(ownerId);
            var disposable = new CountingDisposable();
            lock (Sets) Sets.Add(disposable);
            return Task.FromResult(new SessionToolSet(Tools, disposable: disposable));
        }
    }

    private sealed class CountingDisposable : IAsyncDisposable
    {
        public int Count;
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Count); return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task Ticket_owner_is_passed_to_the_presentation_loader()
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
        var ticket = Guid.NewGuid().ToString("N");
        await factory.Services.GetRequiredService<ITicketStore>().IssueAsync(ticket, "someone-else");
        using var socket = await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
        await BridgeTestSupport.SendAsync(socket, $"{{\"type\":\"auth\",\"ticket\":\"{ticket}\"}}");
        await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "state");

        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        var error = await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "error");

        error["code"]?.GetValue<string>().Should().Be("presentation");
        error["message"]?.GetValue<string>().Should().Contain("cannot load presentation");
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
