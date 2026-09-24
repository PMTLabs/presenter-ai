using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Api.Tests.Infrastructure;
using PresenterAi.Application.Auth;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Scripts.Revisions;
using PresenterAi.Infrastructure.Tests.Live;
using PresenterAi.TestSupport;

namespace PresenterAi.Api.Tests;

/// <summary>
/// Plan 010 T8: the <c>trainer_mode</c>/<c>train_turn</c> commands and the <c>script_edit</c>/<c>script_version</c>
/// frames over the real bridge and presenter (real DI, <see cref="FakeLiveServer"/>). The revision service is the
/// scriptable fake until lane B's service lands; the test plays its worker.
/// </summary>
public sealed class BridgeTrainingTests
{
    private const string Pid = "sample";

    [Fact]
    public async Task Trainer_mode_frame_is_acknowledged_by_script_version()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = TrainingFactory(fake, out _);
        using var socket = await StartTalkAsync(factory);

        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"trainer_mode\",\"on\":true}");
        var frame = await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) == "script_version" && f["trainerMode"]!.GetValue<bool>());
        frame.Select(pair => pair.Key).Should().BeEquivalentTo(
            ["type", "presentationId", "version", "trainerMode", "trainerAvailable", "voiceTraining"]);
        frame["presentationId"]!.GetValue<string>().Should().Be(Pid);
        frame["version"]!.GetValue<int>().Should().Be(1);
        frame["trainerAvailable"]!.GetValue<bool>().Should().BeTrue();
        frame["voiceTraining"]!.GetValue<bool>().Should().BeTrue();

        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"trainer_mode\",\"on\":\"yes\"}");
        (await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) == "error"))["code"]!.GetValue<string>().Should().Be("protocol");
    }

    [Fact]
    public async Task Idle_trainer_toggle_and_the_reset_at_end_reach_the_client_as_trainer_state()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = TrainingFactory(fake, out _);
        using var socket = await BridgeTestSupport.ConnectWithTicketAsync(factory);
        _ = await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) == "state");
        var initial = await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) == "trainer_state");
        initial.Select(pair => pair.Key).Should().BeEquivalentTo(["type", "trainerMode", "trainerAvailable", "voiceTraining"]);
        (initial["trainerMode"]!.GetValue<bool>(), initial["trainerAvailable"]!.GetValue<bool>()).Should().Be((false, true));

        // Idle: the request is stored for the next Start and echoed at once, so the switch shows it.
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"trainer_mode\",\"on\":true}");
        (await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) == "trainer_state"))["trainerMode"]!.GetValue<bool>()
            .Should().BeTrue();

        await BridgeTestSupport.SendAsync(socket, $"{{\"type\":\"start\",\"presentation\":\"{Pid}\"}}");
        (await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) == "script_version"))["trainerMode"]!.GetValue<bool>()
            .Should().BeTrue();

        // End resets Trainer mode on the server; the client hears it.
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"end\"}");
        (await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) == "trainer_state" && !f["trainerMode"]!.GetValue<bool>()))
            ["trainerAvailable"]!.GetValue<bool>().Should().BeTrue();
        factory.Services.GetRequiredService<IPresenter>().CurrentTrainerState().TrainerMode.Should().BeFalse();
    }

    [Fact]
    public async Task Connecting_client_hears_only_its_own_idle_trainer_request()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = TrainingFactory(fake, out _);
        using (var first = await BridgeTestSupport.ConnectAsync(factory))
        {
            await BridgeTestSupport.SendAsync(first, "{\"type\":\"trainer_mode\",\"on\":true}");
            _ = await BridgeTestSupport.ReceiveUntilAsync(first, f => Type(f) == "trainer_state" && f["trainerMode"]!.GetValue<bool>());
            await first.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }

        using var same = await BridgeTestSupport.ConnectWhenFreeAsync(factory);
        (await BridgeTestSupport.ReceiveUntilAsync(same, f => Type(f) == "trainer_state"))["trainerMode"]!.GetValue<bool>()
            .Should().BeTrue();
        await same.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await Task.Delay(100);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (true)
        {
            using var other = await BridgeTestSupport.ConnectWithTicketAsync(factory, userId: "other-user");
            var frame = await BridgeTestSupport.ReceiveAsync(other);
            if (frame.Text is not null && !frame.Text.Contains("\"code\":\"busy\"", StringComparison.Ordinal))
            {
                (await BridgeTestSupport.ReceiveUntilAsync(other, f => Type(f) == "trainer_state"))["trainerMode"]!.GetValue<bool>()
                    .Should().BeFalse();
                return;
            }

            DateTimeOffset.UtcNow.Should().BeBefore(deadline);
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Tool_call_yes_then_applied_emits_script_edit_frames_in_order()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = TrainingFactory(fake, out var service);
        factory.UseFakeClock();
        using var socket = await StartTalkAsync(factory);
        fake.ReceivedSnapshot().Single(m => m["type"]?.GetValue<string>() == "session.start").ToJsonString()
            .Should().Contain("\"revise_script\"");
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"trainer_mode\",\"on\":true}");
        _ = await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) == "script_version" && f["trainerMode"]!.GetValue<bool>());

        await fake.SendFunctionCallAsync("d1", "call1", "revise_script", "{\"feedback\":\"Mention the 2025 figures.\"}");
        _ = await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) == "log" && f["message"]!.ToString().Contains("waiting for yes"));
        // Frames are raised before the handler arms its timer: settle the loop before moving the fake clock.
        await ((Presenter)factory.Services.GetRequiredService<IPresenter>()).WaitUntilIdleAsync();
        await factory.AdvanceAndSettleAsync(TimeSpan.FromSeconds(8));
        await fake.SendEventAsync(new JsonObject
        {
            ["type"] = "session.input_transcript.delta", ["delta"] = "yes", ["start_ms"] = 900_000, ["end_ms"] = 900_100
        });
        _ = await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) == "transcript" && f["delta"]!.ToString() == "yes");
        await ((Presenter)factory.Services.GetRequiredService<IPresenter>()).WaitUntilIdleAsync();
        await factory.AdvanceAndSettleAsync(TimeSpan.FromMilliseconds(701));

        var frames = new List<JsonObject> { await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) == "script_edit") };
        var edit = service.Enqueued.Should().ContainSingle().Subject;
        edit.Request.Feedback.Should().Be("Mention the 2025 figures.");
        service.SetOutcome(edit.Id, EditOutcome.Processing([0]));
        service.RaiseChanged(Pid);
        frames.Add(await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) is "script_edit" or "script_version"));
        var head = service.Head(Pid)!;
        var slides = head.Slides.ToArray();
        slides[0] = slides[0] with { Narration = "Hello everyone, and welcome. The 2025 figures are in." };
        service.SetOutcome(edit.Id, EditOutcome.Applied([0], 2, "Added the 2025 figures"), new HeadSnapshot(Pid, 2, slides));
        service.RaiseChanged(Pid);
        frames.Add(await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) is "script_edit" or "script_version"));
        frames.Add(await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) is "script_edit" or "script_version"));

        frames.Select(f => $"{Type(f)}:{f["status"] ?? f["version"]}").Should().Equal(
            "script_edit:queued", "script_edit:processing", "script_version:2", "script_edit:applied");
        var applied = frames[^1];
        applied.Select(pair => pair.Key).Should().BeEquivalentTo(
            ["type", "id", "status", "slideIndexes", "version", "summary", "error"]);
        applied["id"]!.GetValue<string>().Should().Be(edit.Id);
        applied["slideIndexes"]!.AsArray().Select(n => n!.GetValue<int>()).Should().Equal(0);
        applied["version"]!.GetValue<int>().Should().Be(2);
        applied["summary"]!.GetValue<string>().Should().Be("Added the 2025 figures");
        applied["error"].Should().BeNull();
        await BridgeTestSupport.WaitForAsync(() => fake.ReceivedSnapshot().Any(m =>
            m["content"]?.ToString().Contains("The 2025 figures are in.", StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task Train_turn_frame_creates_an_edit()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = TrainingFactory(fake, out var service);
        using var socket = await StartTalkAsync(factory);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"trainer_mode\",\"on\":true}");
        _ = await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) == "script_version" && f["trainerMode"]!.GetValue<bool>());

        await BridgeTestSupport.SendAsync(socket,
            "{\"type\":\"train_turn\",\"question\":\"What about keys?\",\"answer\":\"Space pauses.\",\"slideIndex\":2}");
        var queued = await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) == "script_edit");
        queued["status"]!.GetValue<string>().Should().Be("queued");
        queued["slideIndexes"]!.AsArray().Select(n => n!.GetValue<int>()).Should().Equal(2);
        var edit = service.Enqueued.Should().ContainSingle().Subject;
        edit.Request.Exchange.Should().Be(new TrainingExchange("What about keys?", "Space pauses."));
        edit.Request.OwnerId.Should().Be("test-user");
    }

    [Theory]
    [InlineData("{\"type\":\"train_turn\",\"question\":\"Q\",\"answer\":5,\"slideIndex\":0}")]
    [InlineData("{\"type\":\"train_turn\",\"question\":\"Q\",\"answer\":\"\",\"slideIndex\":0}")]
    [InlineData("{\"type\":\"train_turn\",\"question\":\"Q\",\"answer\":\"LONG\",\"slideIndex\":0}")]
    [InlineData("{\"type\":\"train_turn\",\"answer\":\"A\",\"slideIndex\":0}")]
    [InlineData("{\"type\":\"train_turn\",\"question\":\"Q\",\"answer\":\"A\",\"slideIndex\":\"0\"}")]
    [InlineData("{\"type\":\"train_turn\",\"question\":\"Q\",\"answer\":\"A\",\"slideIndex\":-1}")]
    [InlineData("{\"type\":\"train_turn\",\"question\":\"Q\",\"answer\":\"A\",\"slideIndex\":3}")]
    [InlineData("{\"type\":\"train_turn\",\"question\":\"Q\",\"answer\":\"A\"}")]
    public async Task Invalid_train_turn_fields_are_protocol_errors(string command)
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = TrainingFactory(fake, out var service);
        using var socket = await StartTalkAsync(factory);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"trainer_mode\",\"on\":true}");
        _ = await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) == "script_version" && f["trainerMode"]!.GetValue<bool>());

        await BridgeTestSupport.SendAsync(socket, command.Replace("LONG", new string('a', 2_001), StringComparison.Ordinal));
        var error = await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) == "error");
        error["code"]!.GetValue<string>().Should().Be("protocol");
        error["message"]!.GetValue<string>().Should().StartWith("train_turn.");
        service.Enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task Vietnamese_train_turn_at_16_KiB_is_accepted_and_one_byte_more_closes_1009()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = TrainingFactory(fake, out var service);
        using var socket = await StartTalkAsync(factory);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"trainer_mode\",\"on\":true}");
        _ = await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) == "script_version" && f["trainerMode"]!.GetValue<bool>());

        var question = Repeat("Lớp biểu bì có mấy tầng? ", 2_000);
        var answer = Repeat("Biểu bì gồm năm lớp tế bào sừng hoá dần. ", 2_000);
        var exact = TrainTurnOfBytes(question, answer, 16 * 1024);
        Encoding.UTF8.GetByteCount(exact).Should().Be(16 * 1024);
        await BridgeTestSupport.SendFragmentedAsync(socket, exact, WebSocketMessageType.Text);
        (await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) == "script_edit"))["status"]!.GetValue<string>()
            .Should().Be("queued");
        service.Enqueued.Should().ContainSingle().Which.Request.Exchange!.Question.Should().Be(question);

        await BridgeTestSupport.SendFragmentedAsync(socket, TrainTurnOfBytes(question, answer, 16 * 1024 + 1),
            WebSocketMessageType.Text);
        await ReceiveCloseStatusAsync(socket, WebSocketCloseStatus.MessageTooBig);
    }

    [Fact]
    public async Task Auth_frame_is_still_limited_to_4_KiB()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = TrainingFactory(fake, out _);
        foreach (var (size, accepted) in new[] { (4 * 1024, true), (4 * 1024 + 1, false) })
        {
            var ticket = Guid.NewGuid().ToString("N");
            await factory.Services.GetRequiredService<ITicketStore>().IssueAsync(ticket, "test-user");
            using var socket = await BridgeTestSupport.ConnectAnonymousAsync(factory);
            var auth = $"{{\"type\":\"auth\",\"ticket\":\"{ticket}\"}}";
            auth = auth[..^1] + new string(' ', size - auth.Length) + "}";
            Encoding.UTF8.GetByteCount(auth).Should().Be(size);
            await BridgeTestSupport.SendAsync(socket, auth);
            if (accepted)
            {
                _ = await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) == "state");
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                await Task.Delay(100);
            }
            else
            {
                await ReceiveCloseStatusAsync(socket, (WebSocketCloseStatus)4401);
            }
        }
    }

    [Fact]
    public async Task Failed_edit_emits_failed_frame_with_error()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = TrainingFactory(fake, out var service);
        using var socket = await StartTalkAsync(factory);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"train_turn\",\"question\":\"Q\",\"answer\":\"A\",\"slideIndex\":0}");
        var refused = await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) == "script_edit");
        (refused["status"]!.GetValue<string>(), refused["error"]!.GetValue<string>()).Should().Be(("failed", "trainer_mode_off"));

        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"trainer_mode\",\"on\":true}");
        _ = await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) == "script_version" && f["trainerMode"]!.GetValue<bool>());
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"train_turn\",\"question\":\"Q\",\"answer\":\"A\",\"slideIndex\":0}");
        _ = await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) == "script_edit" && f["status"]!.ToString() == "queued");
        var id = service.Enqueued.Single().Id;
        service.SetOutcome(id, EditOutcome.Failed([0], ScriptEditErrors.Timeout));
        service.RaiseChanged(Pid);
        var failed = await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) == "script_edit" && f["id"]!.ToString() == id);
        (failed["status"]!.GetValue<string>(), failed["error"]!.GetValue<string>()).Should().Be(("failed", "timeout"));
        failed["version"].Should().BeNull();
        failed["summary"].Should().BeNull();
    }

    [Fact]
    public async Task Reconnecting_client_gets_script_version()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = TrainingFactory(fake, out _);
        using var first = await StartTalkAsync(factory);
        await BridgeTestSupport.SendAsync(first, "{\"type\":\"trainer_mode\",\"on\":true}");
        _ = await BridgeTestSupport.ReceiveUntilAsync(first, f => Type(f) == "script_version" && f["trainerMode"]!.GetValue<bool>());

        // A take-over ends the holder's talk (Trainer mode resets with it); the new client's Start reports the version.
        using var second = await BridgeTestSupport.ConnectWithTicketAsync(factory, takeOver: true);
        _ = await BridgeTestSupport.ReceiveUntilAsync(second, f => Type(f) == "state");
        factory.Services.GetRequiredService<IPresenter>().CurrentScriptVersion().Should().BeNull();
        await BridgeTestSupport.SendAsync(second, $"{{\"type\":\"start\",\"presentation\":\"{Pid}\"}}");
        var version = await BridgeTestSupport.ReceiveUntilAsync(second, f => Type(f) == "script_version");
        (version["presentationId"]!.GetValue<string>(), version["version"]!.GetValue<int>(), version["trainerMode"]!.GetValue<bool>())
            .Should().Be((Pid, 1, false));
    }

    private static ApiFactory TrainingFactory(FakeLiveServer fake, out FakeScriptRevisionService service)
    {
        service = new FakeScriptRevisionService();
        return new ApiFactory
        {
            ScriptRevisions = service,
            Overrides = new Dictionary<string, string?>
            {
                ["Upstream:Endpoint"] = fake.Url,
                ["Upstream:DelegationModel"] = "managed",
                ["Presenter:AdvanceSilenceMs"] = "200"
            }
        };
    }

    private static async Task<WebSocket> StartTalkAsync(ApiFactory factory)
    {
        var socket = await BridgeTestSupport.ConnectAsync(factory);
        await BridgeTestSupport.SendAsync(socket, $"{{\"type\":\"start\",\"presentation\":\"{Pid}\"}}");
        // script_version follows the first slide of a successful Start.
        var version = await BridgeTestSupport.ReceiveUntilAsync(socket, f => Type(f) == "script_version");
        version["trainerMode"]!.GetValue<bool>().Should().BeFalse();
        return socket;
    }

    private static string? Type(JsonObject frame) => frame["type"]?.GetValue<string>();

    private static string Repeat(string text, int maxChars)
    {
        var builder = new StringBuilder();
        while (builder.Length + text.Length <= maxChars) builder.Append(text);
        return builder.ToString().TrimEnd();
    }

    private static string TrainTurnOfBytes(string question, string answer, int bytes)
    {
        var head = $"{{\"type\":\"train_turn\",\"question\":{JsonValue.Create(question).ToJsonString(Unescaped)},";
        var tail = $"\"answer\":{JsonValue.Create(answer).ToJsonString(Unescaped)},\"slideIndex\":1}}";
        var padding = bytes - Encoding.UTF8.GetByteCount(head + tail);
        padding.Should().BePositive();
        return head + new string(' ', padding) + tail;
    }

    private static readonly System.Text.Json.JsonSerializerOptions Unescaped = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static async Task ReceiveCloseStatusAsync(WebSocket socket, WebSocketCloseStatus expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await socket.ReceiveAsync(new byte[32 * 1024], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            if (result.MessageType == WebSocketMessageType.Close)
            {
                result.CloseStatus.Should().Be(expected);
                return;
            }
        }

        throw new TimeoutException("Timed out waiting for the close frame.");
    }
}
