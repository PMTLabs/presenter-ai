using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Api.Tests.Infrastructure;
using PresenterAi.Infrastructure.Tests.Live;

namespace PresenterAi.Api.Tests;

public sealed class BridgeContractTests
{
    [Fact]
    public async Task First_frame_without_a_valid_ticket_closes_4401()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        using var socket = await BridgeTestSupport.ConnectAnonymousAsync(factory);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ping\"}");
        var close = await BridgeTestSupport.ReceiveCloseAsync(socket);
        close.CloseStatus.Should().Be((WebSocketCloseStatus)4401);
        close.CloseStatusDescription.Should().Be("session.ticket_invalid");
    }

    [Fact]
    public async Task Malformed_first_frame_closes_4401()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        using var socket = await BridgeTestSupport.ConnectAnonymousAsync(factory);
        await BridgeTestSupport.SendAsync(socket, "not-json");
        var close = await BridgeTestSupport.ReceiveCloseAsync(socket);
        close.CloseStatus.Should().Be((WebSocketCloseStatus)4401);
    }

    [Fact]
    public async Task Expired_ticket_closes_4401()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        var ticket = Guid.NewGuid().ToString("N");
        var store = factory.Services.GetRequiredService<PresenterAi.Api.Tests.Infrastructure.TestTicketStore>();
        await store.IssueAsync(ticket, "test-user");
        store.Expire(ticket);
        using var socket = await BridgeTestSupport.ConnectAnonymousAsync(factory);
        await BridgeTestSupport.SendAsync(socket, $"{{\"type\":\"auth\",\"ticket\":\"{ticket}\"}}");
        (await BridgeTestSupport.ReceiveCloseAsync(socket)).CloseStatus.Should().Be((WebSocketCloseStatus)4401);
    }

    [Fact]
    public async Task Reused_ticket_closes_4401()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        var ticket = Guid.NewGuid().ToString("N");
        var store = factory.Services.GetRequiredService<PresenterAi.Application.Auth.ITicketStore>();
        await store.IssueAsync(ticket, "test-user");
        (await store.ClaimAsync(ticket)).Should().Be("test-user");
        using var socket = await BridgeTestSupport.ConnectAnonymousAsync(factory);
        await BridgeTestSupport.SendAsync(socket, $"{{\"type\":\"auth\",\"ticket\":\"{ticket}\"}}");
        (await BridgeTestSupport.ReceiveCloseAsync(socket)).CloseStatus.Should().Be((WebSocketCloseStatus)4401);
    }

    [Fact]
    public async Task Auth_timeout_closes_4401()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        factory.Overrides = new Dictionary<string, string?>
        {
            ["Upstream:Endpoint"] = fake.Url,
            ["Session:AuthFrameTimeoutSeconds"] = "1"
        };
        using var socket = await BridgeTestSupport.ConnectAnonymousAsync(factory);
        (await BridgeTestSupport.ReceiveCloseAsync(socket)).CloseStatus.Should().Be((WebSocketCloseStatus)4401);
    }

    [Fact]
    public async Task Stalled_unauthenticated_socket_does_not_occupy_the_slot()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        using var stalled = await BridgeTestSupport.ConnectAnonymousAsync(factory);
        using var valid = await BridgeTestSupport.ConnectAsync(factory);
        valid.State.Should().Be(WebSocketState.Open);
        await stalled.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "cancel", CancellationToken.None);
    }

    [Fact]
    public async Task Ping_gets_pong()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ping\"}");
        (await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "pong")).Select(pair => pair.Key)
            .Should().BeEquivalentTo(["type"]);
    }

    [Fact]
    public async Task Start_without_presentation_is_protocol_error()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"start\"}");
        var error = await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "error");
        error["code"]!.GetValue<string>().Should().Be("protocol");
    }

    [Fact]
    public async Task Auth_frame_is_accepted_and_ignored()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"auth\",\"ticket\":\"dummy\"}");
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ping\"}");
        (await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "pong"))["type"]!.GetValue<string>().Should().Be("pong");
    }

    [Fact]
    public async Task Invalid_json_is_protocol_error()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await BridgeTestSupport.SendAsync(socket, "not-json");
        var error = await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "error");
        error.Select(pair => pair.Key).Should().BeEquivalentTo(["type", "message", "code"]);
        error["code"]!.GetValue<string>().Should().Be("protocol");
    }

    [Fact]
    public async Task Unknown_command_is_protocol_error()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"wat\"}");
        var error = await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "error");
        error.Select(pair => pair.Key).Should().BeEquivalentTo(["type", "message", "code"]);
        error["code"]!.GetValue<string>().Should().Be("protocol");
    }

    [Fact]
    public async Task Plain_get_on_ws_is_400_problem_with_traceparent()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        var response = await client.GetAsync("/ws");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        body["code"]!.GetValue<string>().Should().Be("validation.failed");
        response.Headers.GetValues("traceparent").Single().Should().Be(body["traceId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Canonical_server_frames_have_exact_property_sets()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = BridgeTestSupport.Factory(fake);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"start\",\"presentation\":\"sample\"}");
        var state = await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "state" && frame["state"]?.GetValue<string>() == "presenting");
        state.Select(pair => pair.Key).Should().BeEquivalentTo(["type", "state", "presentationId", "title", "slideIndex", "slideCount", "paused", "muted", "sessionId", "expiresAt", "usageSeconds", "advanceSilenceMs"]);
        (await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "slide")).Select(pair => pair.Key).Should().BeEquivalentTo(["type", "index"]);
        (await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "transcript")).Select(pair => pair.Key).Should().BeEquivalentTo(["type", "role", "delta", "start_ms", "end_ms"]);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"goto\",\"index\":0}");
        (await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "log")).Select(pair => pair.Key).Should().BeEquivalentTo(["type", "level", "message"]);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"bogus\"}");
        (await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "error")).Select(pair => pair.Key).Should().BeEquivalentTo(["type", "message", "code"]);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ping\"}");
        (await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "pong")).Select(pair => pair.Key).Should().BeEquivalentTo(["type"]);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"end\"}");
        (await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "usage")).Select(pair => pair.Key).Should().BeEquivalentTo(["type", "seconds", "ratio"]);
        (await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "closed")).Select(pair => pair.Key).Should().BeEquivalentTo(["type", "reason", "seconds"]);
    }
}
