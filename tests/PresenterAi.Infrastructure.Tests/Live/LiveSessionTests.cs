using System.Text.Json.Nodes;
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
        ILogger<LiveSession>? logger = null)
    {
        return new LiveSession(
            new UpstreamRoute("azure-like", new Uri(server.Url), headers ?? new Dictionary<string, string> { ["Authorization"] = "Bearer test" }, model, delegationModel),
            new LiveSessionConfig(model, "test instructions", "test-voice", presentationTitle),
            clock,
            logger ?? NullLogger<LiveSession>.Instance,
            new LiveSessionOptions { SilencePump = silencePump, CloseTimeout = closeTimeout ?? TimeSpan.FromSeconds(5) });
    }

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

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Information && formatter(state, exception).StartsWith("Delegated response", StringComparison.Ordinal))
            {
                Messages.Add(formatter(state, exception));
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
