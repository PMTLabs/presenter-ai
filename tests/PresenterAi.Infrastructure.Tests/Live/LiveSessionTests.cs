using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PresenterAi.Application.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PresenterAi.Application.Presenting;
using PresenterAi.Infrastructure.Live;

namespace PresenterAi.Infrastructure.Tests.Live;

public sealed class LiveSessionTests
{
    [Fact]
    public async Task Connect_sends_session_start_and_receives_started()
    {
        await using var server = await FakeLiveServer.StartAsync();
        var clock = new FakeTimeProvider();
        await using var session = Create(server, clock, headers: AzureLikeHeaders());

        var info = await session.ConnectAsync();

        info.Id.Should().Be("sess_fake");
        session.State.Should().Be(LiveSessionState.Open);
        var start = await EventuallyAsync(() => server.ReceivedSnapshot().SingleOrDefault(EventTypeIs("session.start")));
        start!["session"]!["model"]!.GetValue<string>().Should().Be("test-model");
        start["session"]!["audio"]!["output"]!["voice"]!.GetValue<string>().Should().Be("test-voice");
        start["session"]!["delegation"]!["type"]!.GetValue<string>().Should().Be("client");
        (await EventuallyAsync(() => server.Headers))!["Authorization"].Should().Be("Bearer test");
        server.Headers!["api-key"].Should().Be("test");
    }

    [Fact]
    public async Task Connect_with_a_cancelled_token_throws_without_opening_a_connection()
    {
        await using var server = await FakeLiveServer.StartAsync();
        await using var session = Create(server, new FakeTimeProvider());

        var action = async () => await session.ConnectAsync(new CancellationToken(canceled: true));

        await action.Should().ThrowAsync<OperationCanceledException>();
        server.Headers.Should().BeNull("no HTTP upgrade request reached the server");
        server.ConnectionCount.Should().Be(0);
        server.ReceivedSnapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task Session_start_sends_responses_delegation_when_a_model_is_set()
    {
        await using var server = await FakeLiveServer.StartAsync();
        await using var session = Create(server, new FakeTimeProvider(), delegationModel: "gpt-5.6-luna", presentationTitle: "Roadmap");

        await session.ConnectAsync();

        var delegation = (await EventuallyAsync(() => server.ReceivedSnapshot().SingleOrDefault(EventTypeIs("session.start"))))!["session"]!["delegation"]!.AsObject();
        delegation.ToJsonString().Should().Be("{\"type\":\"responses\",\"responses\":{\"model\":\"gpt-5.6-luna\",\"instructions\":\"Answer audience questions about the talk titled Roadmap in one to three short spoken sentences; if unsure, say so.\",\"reasoning\":{\"effort\":\"low\"},\"service_tier\":\"priority\",\"text\":{\"verbosity\":\"low\"}}}");
    }

    [Fact]
    public async Task Empty_delegation_model_sends_client_delegation()
    {
        await using var server = await FakeLiveServer.StartAsync();
        await using var session = Create(server, new FakeTimeProvider(), delegationModel: " ");

        await session.ConnectAsync();

        var start = await EventuallyAsync(() => server.ReceivedSnapshot().SingleOrDefault(EventTypeIs("session.start")));
        start!["session"]!["delegation"]!["type"]!.GetValue<string>().Should().Be("client");
    }

    [Fact]
    public async Task Rejected_delegation_retries_once_with_client_delegation()
    {
        await using var server = await FakeLiveServer.StartAsync();
        server.DelegationStartRejections = 1;
        await using var session = Create(server, new FakeTimeProvider(), delegationModel: "gpt-5.6-luna");
        var warnings = new List<string>();
        session.Warning += warnings.Add;
        var errors = new List<string>();
        session.UpstreamError += error => errors.Add(error.GetRawText());

        await session.ConnectAsync();

        var starts = server.ReceivedSnapshot().Where(EventTypeIs("session.start")).ToArray();
        starts.Should().HaveCount(2);
        starts[0]["session"]!["delegation"]!["type"]!.GetValue<string>().Should().Be("responses");
        starts[1]["session"]!["delegation"]!["type"]!.GetValue<string>().Should().Be("client");
        warnings.Should().ContainSingle().Which.Should().Be("delegation: backend unavailable (delegation_unavailable); answering from the deck only");
        errors.Should().BeEmpty("the recovered rejection is reported only as the warning");
    }

    [Fact]
    public async Task Rejected_delegation_followed_by_a_close_still_retries()
    {
        for (var run = 0; run < 5; run++)
        {
            await using var server = await FakeLiveServer.StartAsync();
            server.DelegationStartRejections = 1;
            server.CloseAfterDelegationRejection = true;
            await using var session = Create(server, new FakeTimeProvider(), delegationModel: "gpt-5.6-luna");
            var closed = new List<string>();
            session.Closed += (reason, _) => closed.Add(reason);

            await session.ConnectAsync();

            session.State.Should().Be(LiveSessionState.Open);
            closed.Should().BeEmpty();
            server.ReceivedSnapshot().Where(EventTypeIs("session.start")).Last()["session"]!["delegation"]!["type"]!
                .GetValue<string>().Should().Be("client");
        }
    }

    [Fact]
    public async Task Rejected_delegation_a_second_time_does_not_retry_again()
    {
        await using var server = await FakeLiveServer.StartAsync();
        server.DelegationStartRejections = 2;
        await using var session = Create(server, new FakeTimeProvider(), delegationModel: "gpt-5.6-luna");
        var errors = new List<string>();
        session.UpstreamError += error => errors.Add(error.GetRawText());

        var action = async () => await session.ConnectAsync();

        await action.Should().ThrowAsync<LiveStartupException>();
        server.ReceivedSnapshot().Count(EventTypeIs("session.start")).Should().Be(2);
        errors.Should().ContainSingle("only the unrecovered second rejection is an upstream error");
    }

    [Fact]
    public async Task Other_startup_errors_do_not_retry()
    {
        await using var server = await FakeLiveServer.StartAsync();
        await using var session = Create(server, new FakeTimeProvider(), model: "bad-model", delegationModel: "gpt-5.6-luna");

        var action = async () => await session.ConnectAsync();

        await action.Should().ThrowAsync<LiveStartupException>();
        server.ReceivedSnapshot().Count(EventTypeIs("session.start")).Should().Be(1);
    }

    [Fact]
    public async Task Session_start_includes_tools_only_in_managed_mode()
    {
        var sampleTool = SampleToolDefinition();

        // 1. Managed mode: delegation model set -> tools included under delegation.responses
        await using var serverManaged = await FakeLiveServer.StartAsync();
        await using var sessionManaged = Create(serverManaged, new FakeTimeProvider(), delegationModel: "gpt-5.6-luna", tools: [sampleTool]);
        var infoManaged = await sessionManaged.ConnectAsync();

        infoManaged.DelegationMode.Should().Be("responses");
        var startManaged = await EventuallyAsync(() => serverManaged.ReceivedSnapshot().SingleOrDefault(EventTypeIs("session.start")));
        var responses = startManaged!["session"]!["delegation"]!["responses"]!.AsObject();
        responses["tools"]!.AsArray().Should().HaveCount(1);
        responses["tools"]![0]!["name"]!.GetValue<string>().Should().Be("pause_presentation");
        responses["tool_choice"]!.GetValue<string>().Should().Be("auto");
        responses["parallel_tool_calls"]!.GetValue<bool>().Should().BeFalse();

        // 2. Client mode: delegation model empty -> client delegation, no tools
        await using var serverClient = await FakeLiveServer.StartAsync();
        await using var sessionClient = Create(serverClient, new FakeTimeProvider(), delegationModel: "", tools: [sampleTool]);
        var infoClient = await sessionClient.ConnectAsync();

        infoClient.DelegationMode.Should().Be("client");
        var startClient = await EventuallyAsync(() => serverClient.ReceivedSnapshot().SingleOrDefault(EventTypeIs("session.start")));
        startClient!["session"]!["delegation"]!["type"]!.GetValue<string>().Should().Be("client");
        startClient["session"]!["delegation"]!["responses"].Should().BeNull();
        startClient["session"]!["delegation"]!["tools"].Should().BeNull();
    }

    [Theory]
    [InlineData("gpt-5.6-luna", true)]
    [InlineData("", false)]
    public async Task Hosted_web_search_is_sent_only_in_managed_mode(string delegationModel, bool managed)
    {
        await using var server = await FakeLiveServer.StartAsync();
        await using var session = Create(server, new FakeTimeProvider(), delegationModel: delegationModel,
            hosted: [new JsonObject { ["type"] = "web_search" }]);
        await session.ConnectAsync();
        var start = await EventuallyAsync(() => server.ReceivedSnapshot().SingleOrDefault(EventTypeIs("session.start")));
        var delegation = start!["session"]!["delegation"]!;
        if (managed) delegation["responses"]!["tools"]![0]!["type"]!.GetValue<string>().Should().Be("web_search");
        else delegation["responses"].Should().BeNull();
    }

    [Fact]
    public async Task Web_search_items_raise_hosted_activity_without_exposing_payload()
    {
        await using var server = await FakeLiveServer.StartAsync();
        await using var session = Create(server, new FakeTimeProvider(), delegationModel: "gpt-5.6-luna");
        var statuses = new List<string>();
        session.HostedToolActivity += (_, type, status) => { type.Should().Be("web_search"); statuses.Add(status); };
        await session.ConnectAsync();
        foreach (var (eventType, status) in new[] { ("response.output_item.added", "in_progress"), ("response.output_item.done", "completed") })
            await server.SendEventAsync(new JsonObject { ["type"] = "response.event", ["delegation_id"] = "d", ["event"] = new JsonObject {
                ["type"] = eventType, ["item"] = new JsonObject { ["type"] = "web_search_call", ["status"] = status, ["query"] = "private query" } } });
        await EventuallyAsync(() => statuses.Count == 2);
        statuses.Should().Equal("in_progress", "completed");
    }

    [Fact]
    public async Task Hundred_registered_tools_real_session_start_has_only_pinned_and_meta_within_budget()
    {
        var registry = new ToolRegistry();
        for (var i = 0; i < 100; i++) registry.Register(new BudgetTool($"tool_{i:D3}", i < 6));
        var catalogue = registry.CreateCatalogue(16);
        await using var server = await FakeLiveServer.StartAsync();
        await using var session = Create(server, new FakeTimeProvider(), delegationModel: "backend", tools: catalogue.GetInlineToolDefinitions());
        await session.ConnectAsync();
        var start = (await EventuallyAsync(() => server.ReceivedSnapshot().SingleOrDefault(EventTypeIs("session.start"))))!;
        var definitions = start["session"]!["delegation"]!["responses"]!["tools"]!.AsArray();
        definitions.Select(d => d!["name"]!.GetValue<string>()).Should().BeEquivalentTo(
            Enumerable.Range(0, 6).Select(i => $"tool_{i:D3}").Concat(["find_tools", "call_tool"]));
        // The 32 KiB limit is for the serialized tool list, not the whole session envelope.
        Encoding.UTF8.GetByteCount(definitions.ToJsonString()).Should().BeLessThan(32 * 1024);
        Encoding.UTF8.GetByteCount(start.ToJsonString()).Should().BeLessThan(36 * 1024);
    }

    private sealed class BudgetTool(string name, bool pinned) : ITool
    {
        public string Name => name;
        public string Description => "Budget test tool";
        public JsonObject Parameters { get; } = new() { ["type"] = "object", ["properties"] = new JsonObject() };
        public IReadOnlyList<string> Tags => ["test"];
        public bool Pinned => pinned;
        public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    [Fact]
    public async Task Function_call_item_done_raises_ToolCallRequested_event()
    {
        await using var server = await FakeLiveServer.StartAsync();
        await using var session = Create(server, new FakeTimeProvider(), delegationModel: "gpt-5.6-luna");
        string? receivedDelegationId = null;
        string? receivedCallId = null;
        string? receivedName = null;
        string? receivedArgs = null;

        session.ToolCallRequested += (delId, callId, name, args) =>
        {
            receivedDelegationId = delId;
            receivedCallId = callId;
            receivedName = name;
            receivedArgs = args;
        };

        await session.ConnectAsync();
        await server.SendFunctionCallAsync("del_123", "call_abc", "pause_presentation", "{\"param\":\"val\"}");

        await EventuallyAsync(() => receivedCallId is not null);
        receivedDelegationId.Should().Be("del_123");
        receivedCallId.Should().Be("call_abc");
        receivedName.Should().Be("pause_presentation");
        receivedArgs.Should().Be("{\"param\":\"val\"}");
    }

    [Fact]
    public async Task SubmitToolOutput_sends_response_item_create_before_ContinueResponses_sends_response_create()
    {
        await using var server = await FakeLiveServer.StartAsync();
        await using var session = Create(server, new FakeTimeProvider(), delegationModel: "gpt-5.6-luna");
        await session.ConnectAsync();

        var outputSubmitted = session.SubmitToolOutput("call_abc", "{\"ok\":true}");
        outputSubmitted.Should().BeTrue();

        var continued = session.ContinueResponses();
        continued.Should().BeTrue();

        await EventuallyAsync(() =>
        {
            var msgs = server.ReceivedSnapshot();
            return msgs.Any(EventTypeIs("response.item.create")) && msgs.Any(EventTypeIs("response.create"));
        });

        var snapshot = server.ReceivedSnapshot().ToList();
        var itemCreateIdx = snapshot.FindIndex(m => Type(m) == "response.item.create");
        var respCreateIdx = snapshot.FindIndex(m => Type(m) == "response.create");

        itemCreateIdx.Should().BeGreaterThanOrEqualTo(0);
        respCreateIdx.Should().BeGreaterThan(itemCreateIdx);

        var itemCreate = snapshot[itemCreateIdx];
        itemCreate["item"]!["type"]!.GetValue<string>().Should().Be("function_call_output");
        itemCreate["item"]!["call_id"]!.GetValue<string>().Should().Be("call_abc");
        itemCreate["item"]!["output"]!.GetValue<string>().Should().Be("{\"ok\":true}");
    }

    [Fact]
    public async Task Tools_rejection_falls_back_to_client_mode_with_SessionInfo_saying_client()
    {
        await using var server = await FakeLiveServer.StartAsync();
        server.ToolsStartRejections = 1;
        var warnings = new List<string>();
        var errors = new List<string>();

        await using var session = Create(server, new FakeTimeProvider(), delegationModel: "gpt-5.6-luna", tools: [SampleToolDefinition()]);
        session.Warning += warnings.Add;
        session.UpstreamError += error => errors.Add(error.GetRawText());

        var info = await session.ConnectAsync();

        info.DelegationMode.Should().Be("client");
        session.State.Should().Be(LiveSessionState.Open);

        var starts = server.ReceivedSnapshot().Where(EventTypeIs("session.start")).ToArray();
        starts.Should().HaveCount(2);
        starts[0]["session"]!["delegation"]!["type"]!.GetValue<string>().Should().Be("responses");
        starts[0]["session"]!["delegation"]!["responses"]!["tools"].Should().NotBeNull();

        starts[1]["session"]!["delegation"]!["type"]!.GetValue<string>().Should().Be("client");
        starts[1]["session"]!["delegation"]!["responses"].Should().BeNull();

        warnings.Should().ContainSingle().Which.Should().Contain("tools_not_supported");
        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Delegation_event_preserves_id_target_and_offset()
    {
        await using var server = await FakeLiveServer.StartAsync();
        await using var session = Create(server, new FakeTimeProvider());
        var delegations = new List<System.Text.Json.JsonElement>();
        session.Delegation += delegations.Add;
        await session.ConnectAsync();

        await server.SendEventAsync(new JsonObject
        {
            ["type"] = "session.delegation.created",
            ["event_id"] = "event-1",
            ["offset_ms"] = 1234,
            ["delegation"] = new JsonObject { ["id"] = "delegation-1", ["type"] = "delegation", ["target"] = "client" }
        });
        await EventuallyAsync(() => delegations.Count == 1);

        delegations.Single().GetProperty("offset_ms").GetInt64().Should().Be(1234);
        delegations.Single().GetProperty("delegation").GetProperty("id").GetString().Should().Be("delegation-1");
        delegations.Single().GetProperty("delegation").GetProperty("target").GetString().Should().Be("client");
    }

    [Fact]
    public async Task Response_event_completion_and_error_are_logged()
    {
        await using var server = await FakeLiveServer.StartAsync();
        var logger = new RecordingLogger<LiveSession>();
        await using var session = Create(server, new FakeTimeProvider(), logger: logger);
        var finished = new List<string>();
        session.DelegatedResponseFinished += (id, type) => finished.Add($"{id}:{type}");
        await session.ConnectAsync();

        await server.SendEventAsync(new JsonObject
        {
            ["type"] = "response.event",
            ["delegation_id"] = "delegation-1",
            ["event"] = new JsonObject { ["type"] = "response.completed" }
        });
        await server.SendEventAsync(new JsonObject
        {
            ["type"] = "response.event",
            ["delegation_id"] = "delegation-2",
            ["event"] = new JsonObject { ["type"] = "response.failed" }
        });
        await EventuallyAsync(() => logger.Messages.Count == 2 ? logger.Messages : null);

        logger.Messages.Should().Contain(message => message.Contains("Delegated response completed: id=delegation-1", StringComparison.Ordinal));
        logger.Messages.Should().Contain(message => message.Contains("Delegated response failed: id=delegation-2 type=response.failed", StringComparison.Ordinal));
        await EventuallyAsync(() => finished.Count == 2 ? true : false);
        finished.Should().Equal("delegation-1:response.completed", "delegation-2:response.failed");
    }

    [Fact]
    public async Task Startup_error_closes_socket_and_finishes()
    {
        await using var server = await FakeLiveServer.StartAsync();
        var closed = new List<string>();
        await using var session = Create(server, new FakeTimeProvider(), model: "bad-model");
        session.Closed += (reason, _) => closed.Add(reason);

        var action = async () => await session.ConnectAsync();

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*unknown model*");
        await EventuallyAsync(() => server.ConnectionCount == 0 ? true : false);
        closed.Should().ContainSingle().Which.Should().Be("startup_error");
        session.State.Should().Be(LiveSessionState.Closed);
    }

    [Fact]
    public async Task Pump_sends_only_the_gap_not_every_tick()
    {
        await using var server = await FakeLiveServer.StartAsync();
        var clock = new FakeTimeProvider();
        await using var session = Create(server, clock);
        await session.ConnectAsync();
        await WaitForPumpAsync();

        for (var tick = 0; tick < 25; tick++)
        {
            await TickAsync(clock);
        }

        var settled = await SettledSilenceFrameCountAsync(server, atLeast: 19);
        settled.Should().BeInRange(19, 20, "the Node pump preserves 120 ms slack");

        var silenceBeforeAudio = SilenceFrames(server).Count;
        for (var frame = 0; frame < 10; frame++)
        {
            session.SendAudio(Enumerable.Repeat((byte)1, 960).ToArray());
            await TickAsync(clock);
        }

        await EventuallyAsync(() => VoiceFrames(server).Count >= 10 ? true : false);
        SilenceFrames(server).Count.Should().Be(silenceBeforeAudio);
    }

    [Fact]
    public async Task Pump_catches_up_after_delayed_tick()
    {
        await using var server = await FakeLiveServer.StartAsync();
        var clock = new FakeTimeProvider();
        await using var session = Create(server, clock);
        await session.ConnectAsync();
        await WaitForPumpAsync();

        clock.Advance(TimeSpan.FromMilliseconds(300));
        // (300 - 120 slack) / 20 = 9 frames, sent as one burst; wait for the whole burst to be observed
        // and let the socket settle before counting, otherwise the assertion races the fake's recorder.
        var burst = await SettledSilenceFrameCountAsync(server, atLeast: 9);

        burst.Should().BeInRange(8, 10, "the Node pump retains 120 ms slack and catches up in one bounded burst");
    }

    [Fact]
    public async Task SilenceMs_is_monotonic_and_matches_bytes_sent()
    {
        await using var server = await FakeLiveServer.StartAsync();
        var clock = new FakeTimeProvider();
        await using var session = Create(server, clock);
        await session.ConnectAsync();
        await WaitForPumpAsync();

        for (var tick = 0; tick < 10; tick++)
        {
            await TickAsync(clock);
        }

        var first = session.SilenceMs;
        for (var tick = 0; tick < 10; tick++)
        {
            await TickAsync(clock);
        }

        session.SilenceMs.Should().BeGreaterOrEqualTo(first);
        var expectedFrames = (int)(session.SilenceMs / 20);
        var recorded = await SettledSilenceFrameCountAsync(server, atLeast: expectedFrames);
        session.SilenceMs.Should().Be(recorded * 20);
    }

    [Fact]
    public async Task Mute_and_unmute_are_forwarded()
    {
        await using var server = await FakeLiveServer.StartAsync();
        var clock = new FakeTimeProvider();
        await using var session = Create(server, clock);
        await session.ConnectAsync();
        await WaitForPumpAsync();

        session.Mute().Should().BeTrue();
        session.Unmute().Should().BeTrue();
        await EventuallyAsync(() => server.ReceivedSnapshot().Count(EventTypeIs("session.input_audio.unmute")) == 1 ? true : false);
        for (var tick = 0; tick < 10; tick++)
        {
            await TickAsync(clock);
        }

        server.ReceivedSnapshot().Should().Contain(message => Type(message) == "session.input_audio.mute");
        server.ReceivedSnapshot().Should().Contain(message => Type(message) == "session.input_audio.unmute");
        SilenceFrames(server).Should().NotBeEmpty("muting does not disable the Node silence pump");
    }

    [Fact]
    public async Task Unmuted_server_event_raises_InputAudioUnmuted()
    {
        // Plan 011 (P-18): Ask done waits for this ack before the burst, so the receive switch must raise it.
        await using var server = await FakeLiveServer.StartAsync();
        await using var session = Create(server, new FakeTimeProvider());
        var acks = 0;
        string? ackedId = null;
        session.InputAudioUnmuted += id =>
        {
            Volatile.Write(ref ackedId, id);
            Interlocked.Increment(ref acks);
        };
        ILiveSession port = session;
        var viaPort = 0;
        port.InputAudioUnmuted += _ => Interlocked.Increment(ref viaPort);
        await session.ConnectAsync();

        session.Mute().Should().BeTrue();
        await EventuallyAsync(() => server.ReceivedSnapshot().Any(EventTypeIs("session.input_audio.mute")));
        Volatile.Read(ref acks).Should().Be(0, "a mute is not acknowledged as unmuted");

        session.Unmute(out var unmuteId).Should().BeTrue();
        unmuteId.Should().StartWith("unmute-");

        // Both handlers run in turn on the receive loop; waiting only for the first raced the second under load.
        (await EventuallyAsync(() => Volatile.Read(ref acks) == 1 && Volatile.Read(ref viaPort) == 1)).Should().BeTrue();
        Volatile.Read(ref ackedId).Should().Be(unmuteId, "the ack carries the unmute's echoed client_event_id (review r1 #1)");
        Volatile.Read(ref viaPort).Should().Be(1, "the ILiveSession event is the one the presenter subscribes to");
    }

    [Fact]
    public async Task Close_returns_usage_and_reason()
    {
        await using var server = await FakeLiveServer.StartAsync();
        await using var session = Create(server, new FakeTimeProvider());
        var closed = new List<LiveCloseResult>();
        session.Closed += (reason, seconds) => closed.Add(new LiveCloseResult(reason, seconds));
        await session.ConnectAsync();

        var result = await session.CloseAsync();

        result.Should().Be(new LiveCloseResult("client_request", 7));
        closed.Should().ContainSingle().Which.Should().Be(result);
    }

    [Fact]
    public async Task Close_times_out_and_aborts_when_server_is_silent()
    {
        await using var server = await FakeLiveServer.StartAsync();
        server.IgnoreClose = true;
        var clock = new FakeTimeProvider();
        await using var session = Create(server, clock, closeTimeout: TimeSpan.FromSeconds(5));
        await session.ConnectAsync();

        var close = session.CloseAsync();
        await EventuallyAsync(() => server.ReceivedSnapshot().Any(EventTypeIs("session.close")) ? true : false);
        clock.Advance(TimeSpan.FromSeconds(5));
        var result = await close;

        result.Should().Be(new LiveCloseResult("connection_lost", null));
        await EventuallyAsync(() => server.ConnectionCount == 0 ? true : false);
    }

    [Fact]
    public async Task Dispose_logs_upstream_disposal_with_session_id()
    {
        await using var server = await FakeLiveServer.StartAsync();
        var logger = new RecordingLogger<LiveSession>(message =>
            message.StartsWith("Upstream socket disposed:", StringComparison.Ordinal));
        var session = Create(server, new FakeTimeProvider(), logger: logger);

        await session.ConnectAsync();
        session.Id.Should().Be("sess_fake");

        await session.DisposeAsync();

        logger.Messages.Should().ContainSingle()
            .Which.Should().Be("Upstream socket disposed: session=sess_fake route=azure-like state=Closed");
    }

    [Fact]
    public async Task Transport_loss_finishes_once_with_connection_lost()
    {
        await using var server = await FakeLiveServer.StartAsync();
        await using var session = Create(server, new FakeTimeProvider());
        var closed = new List<string>();
        session.Closed += (reason, _) => closed.Add(reason);
        await session.ConnectAsync();

        server.DropAll();
        await EventuallyAsync(() => closed.Count == 1 ? true : false);

        closed.Should().ContainSingle().Which.Should().Be("connection_lost");
    }

    [Fact]
    public async Task No_ticks_after_finish()
    {
        await using var server = await FakeLiveServer.StartAsync();
        var clock = new FakeTimeProvider();
        await using var session = Create(server, clock);
        await session.ConnectAsync();
        await WaitForPumpAsync();
        for (var tick = 0; tick < 10; tick++)
        {
            await TickAsync(clock);
        }

        server.DropAll();
        await EventuallyAsync(() => session.State == LiveSessionState.Closed ? true : false);
        var countAtFinish = server.ReceivedSnapshot().Count;
        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        server.ReceivedSnapshot().Count.Should().Be(countAtFinish);
    }

    [Fact]
    public async Task Audio_odd_byte_dropped_and_empty_not_sent()
    {
        await using var server = await FakeLiveServer.StartAsync();
        await using var session = Create(server, new FakeTimeProvider(), silencePump: false);
        await session.ConnectAsync();

        session.SendAudio(ReadOnlyMemory<byte>.Empty).Should().BeFalse();
        session.SendAudio(new byte[961].Select(_ => (byte)1).ToArray()).Should().BeTrue();
        await EventuallyAsync(() => VoiceFrames(server).Count == 1 ? true : false);

        VoiceFrames(server).Single()["audioLength"]!.GetValue<int>().Should().Be(1280);
    }

    private static LiveSession Create(
        FakeLiveServer server,
        TimeProvider clock,
        string model = "test-model",
        IReadOnlyDictionary<string, string>? headers = null,
        bool silencePump = true,
        TimeSpan? closeTimeout = null,
        string delegationModel = "",
        string? presentationTitle = null,
        ILogger<LiveSession>? logger = null,
        IReadOnlyList<JsonObject>? tools = null,
        IReadOnlyList<JsonObject>? hosted = null)
    {
        return new LiveSession(
            new UpstreamRoute("azure-like", new Uri(server.Url), headers ?? new Dictionary<string, string> { ["Authorization"] = "Bearer test" }, model, delegationModel),
            new LiveSessionConfig(model, "test instructions", "test-voice", presentationTitle, tools, HostedTools: hosted),
            clock,
            logger ?? NullLogger<LiveSession>.Instance,
            new LiveSessionOptions { SilencePump = silencePump, CloseTimeout = closeTimeout ?? TimeSpan.FromSeconds(5) });
    }

    private static JsonObject SampleToolDefinition(string name = "pause_presentation") => new()
    {
        ["type"] = "function",
        ["name"] = name,
        ["description"] = "Pause presentation",
        ["parameters"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
            ["additionalProperties"] = false
        }
    };

    private static IReadOnlyDictionary<string, string> AzureLikeHeaders()
    {
        return new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer test",
            ["api-key"] = "test"
        };
    }

    private static async Task WaitForPumpAsync()
    {
        await Task.Delay(20);
    }

    private static async Task TickAsync(FakeTimeProvider clock)
    {
        clock.Advance(TimeSpan.FromMilliseconds(20));
        await Task.Delay(5);
    }

    /// <summary>
    /// Waits until the fake has recorded at least <paramref name="atLeast"/> silence frames, then lets the
    /// socket drain for a moment and returns the settled count (so over-sending is still detected).
    /// </summary>
    private static async Task<int> SettledSilenceFrameCountAsync(FakeLiveServer server, int atLeast)
    {
        await EventuallyAsync(() => SilenceFrames(server).Count >= atLeast);
        await Task.Delay(50);
        return SilenceFrames(server).Count;
    }

    private static List<JsonObject> SilenceFrames(FakeLiveServer server)
    {
        return server.ReceivedSnapshot().Where(message => Type(message) == "session.input_audio.append" && message["silent"]!.GetValue<bool>()).ToList();
    }

    private static List<JsonObject> VoiceFrames(FakeLiveServer server)
    {
        return server.ReceivedSnapshot().Where(message => Type(message) == "session.input_audio.append" && !message["silent"]!.GetValue<bool>()).ToList();
    }

    private static Func<JsonObject, bool> EventTypeIs(string type)
    {
        return message => Type(message) == type;
    }

    private static string? Type(JsonObject message)
    {
        return message["type"]?.GetValue<string>();
    }

    private static async Task<T> EventuallyAsync<T>(Func<T?> value, int timeoutMs = 2000)
        where T : class
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            var result = value();
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for fake live server observation.");
    }

    private sealed class RecordingLogger<T>(Func<string, bool>? filter = null) : ILogger<T>
    {
        private readonly Func<string, bool> _filter = filter ?? (message => message.StartsWith("Delegated response", StringComparison.Ordinal));
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Information)
            {
                var message = formatter(state, exception);
                if (_filter(message))
                {
                    Messages.Add(message);
                }
            }
        }
    }

    private static async Task<bool> EventuallyAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for fake live server observation.");
    }
}
