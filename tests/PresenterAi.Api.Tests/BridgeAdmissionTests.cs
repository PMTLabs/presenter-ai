using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Api.Realtime;
using PresenterAi.Api.Tests.Infrastructure;
using PresenterAi.Application.Presenting;
using PresenterAi.Infrastructure.Tests.Live;

namespace PresenterAi.Api.Tests;

/// <summary>
/// Plan 011 T5 (P-10, P-14): ordered per-connection admission. The receive loop never waits; one pump makes at most one
/// presenter call at a time in socket order; a full queue closes 1011; End bypasses the queue; disconnect drops it.
/// </summary>
public sealed class BridgeAdmissionTests
{
    [Fact]
    public async Task Admission_queue_filled_to_capacity_records_exactly_the_pcm_before_ask_done_in_order()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        fake.KeepAudio = true;
        using var factory = AskSupport.Factory(fake,
            ("Session:HeartbeatTimeoutSeconds", "300"), ("Session:HeartbeatIntervalSeconds", "60"));
        factory.UseFakeClock();
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await AskSupport.StartTalkAsync(socket);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"pause\"}");
        await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["state"]?.GetValue<string>() == "paused");
        await factory.AdvanceAndSettleAsync(TimeSpan.FromSeconds(121));
        await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "upstream" &&
            frame["status"]?.GetValue<string>() == "suspended");
        await BridgeTestSupport.WaitForAsync(() => fake.ConnectionCount == 0);

        // Ask from suspended: the presenter loop is held in the reconnect until the upstream answers session.start.
        fake.StartGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_start\"}");
        await BridgeTestSupport.WaitForAsync(() => fake.StartCount == 2);

        var question = Enumerable.Range(0, 200).Select(AskSupport.VoicedFrame).ToArray();
        foreach (var frame in question) await AskSupport.SendBinaryAsync(socket, frame);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_done\"}");
        foreach (var frame in Enumerable.Range(10_000, 55).Select(AskSupport.VoicedFrame))
            await AskSupport.SendBinaryAsync(socket, frame);
        // 200 + 1 + 55 = 256 queued items: exactly the capacity, so nothing overflowed and the socket stays open.
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ping\"}");
        await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "pong");
        socket.State.Should().Be(WebSocketState.Open);

        fake.StartGate.TrySetResult();
        (await AskSupport.ReceiveAskStateAsync(socket, "answering"))["reason"]!.GetValue<string>().Should().Be("sent");
        var expected = AskSupport.ExpectedBurst(question);
        await BridgeTestSupport.WaitForAsync(() =>
            AskSupport.BurstAppends(fake.ReceivedSnapshot()).Sum(append => append.Length) >= expected.Length);

        AskSupport.BurstAppends(fake.ReceivedSnapshot()).SelectMany(bytes => bytes).ToArray().Should().Equal(expected,
            "the recording holds exactly the 200 frames sent before ask_done, in order, and none sent after it");
    }

    [Fact]
    public async Task Admission_calls_the_presenter_one_at_a_time_in_exact_socket_order()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        var presenter = new RecordingPresenter();
        using var factory = BridgeTestSupport.Factory(fake);
        factory.PresenterOverride = presenter;
        using var socket = await BridgeTestSupport.ConnectAsync(factory);

        var sent = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            foreach (var (frame, expected) in Commands(i))
            {
                if (frame is null) await AskSupport.SendBinaryAsync(socket, AskSupport.VoicedFrame(i));
                else await BridgeTestSupport.SendAsync(socket, frame);
                sent.Add(expected);
            }
        }

        await BridgeTestSupport.WaitForAsync(() => presenter.Calls.Count == sent.Count);
        presenter.Calls.Should().Equal(sent, "the loop sees admitted items in socket order");

        // More items, then the browser goes away while they are still being admitted.
        var tail = Enumerable.Range(100, 100).Select(seed => $"audio:{seed}").ToList();
        foreach (var seed in Enumerable.Range(100, 100)) await AskSupport.SendBinaryAsync(socket, AskSupport.VoicedFrame(seed));
        var released = factory.Services.GetRequiredService<PresenterBridge>().CurrentReleasedForTestAsync();
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "disconnect", CancellationToken.None);
        await released.WaitAsync(TimeSpan.FromSeconds(10));

        presenter.Ended.Should().BeTrue();
        presenter.Calls.Take(sent.Count).Should().Equal(sent);
        presenter.Calls.Skip(sent.Count).Should().Equal(tail.Take(presenter.Calls.Count - sent.Count),
            "whatever was admitted before disposal is a prefix of the socket order");
        presenter.CallsStartedAfterEnd.Should().Be(0, "no admitted call starts after disconnect cleanup began");
        presenter.MaxInFlight.Should().Be(1, "at most one presenter call is ever in flight");
    }

    [Fact]
    public async Task Full_admission_queue_closes_1011_and_ends_the_talk_promptly_without_a_burst()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = AskSupport.Factory(fake, ("Session:HeartbeatTimeoutSeconds", "300"));
        var gate = AdmissionGate.Install(factory);
        var presenter = factory.Services.GetRequiredService<IPresenter>();
        var closed = new TaskCompletionSource<PresenterClosed>(TaskCreationOptions.RunContinuationsAsynchronously);
        presenter.Closed += value => closed.TrySetResult(value);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await AskSupport.StartTalkAsync(socket);
        await AskSupport.AskWithSpeechAsync(socket, voicedFrames: 20);

        gate.Close();
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_done\"}");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // ask_done is held at the pump's item boundary; 256 more fill the queue and the next one overflows it.
        for (var seed = 0; seed <= BridgeAdmissionCapacity; seed++)
            await AskSupport.SendBinaryAsync(socket, AskSupport.VoicedFrame(seed));
        var watch = Stopwatch.StartNew();

        var close = await BridgeTestSupport.ReceiveUntilCloseAsync(socket);
        close.CloseStatus.Should().Be((WebSocketCloseStatus)1011);
        close.CloseStatusDescription.Should().Be("backpressure");
        (await closed.Task.WaitAsync(TimeSpan.FromSeconds(5))).EndReason.Should().Be(EndReasons.Backpressure);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the talk ends within the drain bound, not by heartbeat");
        presenter.Snapshot().State.Should().Be("idle");
        gate.Release();
        await Task.Delay(200);
        AskSupport.BurstAppends(fake.ReceivedSnapshot()).Should().BeEmpty("the queued ask_done was dropped");
    }

    [Fact]
    public async Task End_frame_with_the_pump_blocked_and_the_queue_nearly_full_is_read_and_ends_the_talk_promptly()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = AskSupport.Factory(fake, ("Session:HeartbeatTimeoutSeconds", "300"));
        var gate = AdmissionGate.Install(factory);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await AskSupport.StartTalkAsync(socket);
        await AskSupport.AskWithSpeechAsync(socket, voicedFrames: 20);

        gate.Close();
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_done\"}");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var seed = 0; seed < BridgeAdmissionCapacity - 1; seed++)
            await AskSupport.SendBinaryAsync(socket, AskSupport.VoicedFrame(seed));
        var watch = Stopwatch.StartNew();
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"end\"}");

        var frames = new List<(JsonObject? Text, byte[]? Binary)>();
        var closedAt = await AskSupport.CollectForAsync(socket, frames, TimeSpan.FromSeconds(5),
            frame => frame["type"]?.GetValue<string>() == "closed");
        closedAt.Should().BeGreaterThanOrEqualTo(0, "the end frame is read at once although the pump is blocked");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        frames[closedAt].Text!["endReason"]!.GetValue<string>().Should().Be(EndReasons.User);
        frames.Take(closedAt).Should().Contain(frame => frame.Text != null &&
            frame.Text["type"]!.GetValue<string>() == "ask_state" && frame.Text["reason"]!.GetValue<string>() == "ended");

        gate.Release();
        await ((Presenter)factory.Services.GetRequiredService<IPresenter>()).WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        AskSupport.BurstAppends(fake.ReceivedSnapshot()).Should().BeEmpty();
    }

    [Fact]
    public async Task Disconnect_while_items_wait_for_admission_drops_them_and_ends_the_talk_before_release()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = AskSupport.Factory(fake);
        var gate = AdmissionGate.Install(factory);
        var presenter = factory.Services.GetRequiredService<IPresenter>();
        var closed = new TaskCompletionSource<PresenterClosed>(TaskCreationOptions.RunContinuationsAsynchronously);
        presenter.Closed += value => closed.TrySetResult(value);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await AskSupport.StartTalkAsync(socket);
        await AskSupport.AskWithSpeechAsync(socket, voicedFrames: 20);

        gate.Close();
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_done\"}");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var seed = 0; seed < 50; seed++) await AskSupport.SendBinaryAsync(socket, AskSupport.VoicedFrame(seed));
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "disconnect", CancellationToken.None);

        (await closed.Task.WaitAsync(TimeSpan.FromSeconds(5))).EndReason.Should().Be(EndReasons.Disconnect,
            "the talk ends while the gate still holds the queued items");
        await BridgeTestSupport.WaitForAsync(() => presenter.Snapshot().State == "idle");
        gate.Release();
        await ((Presenter)factory.Services.GetRequiredService<IPresenter>()).WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        AskSupport.BurstAppends(fake.ReceivedSnapshot()).Should().BeEmpty();
        var received = fake.ReceivedSnapshot();
        received.Skip(AskSupport.IndexOf(received, "session.input_audio.mute")).Should().NotContain(message =>
            message["type"]!.GetValue<string>() == "session.input_audio.unmute");
    }

    [Fact]
    public async Task End_bypasses_admission_and_a_queued_ask_done_is_ignored_afterwards()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = AskSupport.Factory(fake);
        var gate = AdmissionGate.Install(factory);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await AskSupport.StartTalkAsync(socket);
        await AskSupport.AskWithSpeechAsync(socket, voicedFrames: 20);

        gate.Close();
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_done\"}");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"end\"}");
        var frames = new List<(JsonObject? Text, byte[]? Binary)>();
        var closedAt = await AskSupport.CollectForAsync(socket, frames, TimeSpan.FromSeconds(5),
            frame => frame["type"]?.GetValue<string>() == "closed");
        closedAt.Should().BeGreaterThanOrEqualTo(0);

        // Release the queued ask_done, then an ask_start behind it: its refusal proves ask_done was delivered first.
        gate.Release();
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_start\"}");
        frames.Clear();
        var refused = await AskSupport.CollectForAsync(socket, frames, TimeSpan.FromSeconds(5),
            frame => frame["type"]?.GetValue<string>() == "ask_state");
        refused.Should().BeGreaterThanOrEqualTo(0);
        frames[refused].Text!["state"]!.GetValue<string>().Should().Be("off");
        frames[refused].Text!["reason"]!.GetValue<string>().Should().Be("refused_not_live",
            "the ask_done delivered after End produced no frame of its own");
        AskSupport.BurstAppends(fake.ReceivedSnapshot()).Should().BeEmpty();
        var received = fake.ReceivedSnapshot();
        received.Skip(AskSupport.IndexOf(received, "session.input_audio.mute")).Should().NotContain(message =>
            message["type"]!.GetValue<string>() == "session.input_audio.unmute");
    }

    [Fact]
    public async Task Max_length_while_ask_done_waits_for_admission_ends_the_talk_without_a_burst()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = AskSupport.Factory(fake);
        factory.UseFakeClock();
        var gate = AdmissionGate.Install(factory);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await AskSupport.StartTalkAsync(socket, ",\"maxMinutes\":5");
        for (var step = 0; step < 8; step++) await KeepAliveAsync(factory, socket);
        await AskSupport.AskWithSpeechAsync(socket, voicedFrames: 20);

        gate.Close();
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_done\"}");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await KeepAliveAsync(factory, socket);
        await factory.AdvanceAndSettleAsync(TimeSpan.FromSeconds(31));
        var closed = await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "closed");
        closed["endReason"]!.GetValue<string>().Should().Be(EndReasons.MaxLength);

        gate.Release();
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_start\"}");
        (await AskSupport.ReceiveAskStateAsync(socket, "off"))["reason"]!.GetValue<string>().Should().Be("refused_not_live");
        AskSupport.BurstAppends(fake.ReceivedSnapshot()).Should().BeEmpty();
        var received = fake.ReceivedSnapshot();
        received.Skip(AskSupport.IndexOf(received, "session.input_audio.mute")).Should().NotContain(message =>
            message["type"]!.GetValue<string>() == "session.input_audio.unmute");
    }

    private const int BridgeAdmissionCapacity = PresenterBridge.AdmissionCapacity;

    private static async Task KeepAliveAsync(ApiFactory factory, WebSocket socket)
    {
        await factory.AdvanceAndSettleAsync(TimeSpan.FromSeconds(30));
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ping\"}");
        await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "pong");
    }

    /// <summary>One cycle of admitted frames (null = a binary audio frame) and the call each must produce.</summary>
    private static IEnumerable<(string? Frame, string Expected)> Commands(int i) =>
    [
        (null, $"audio:{i}"),
        ("{\"type\":\"pause\"}", "pause"),
        ("{\"type\":\"resume\"}", "resume"),
        ($"{{\"type\":\"goto\",\"index\":{i}}}", $"goto:{i}"),
        (null, $"audio:{i}"),
        ("{\"type\":\"mute\"}", "mute"),
        ("{\"type\":\"unmute\"}", "unmute"),
        ("{\"type\":\"next\"}", "next"),
        ("{\"type\":\"prev\"}", "prev"),
        ("{\"type\":\"ask_start\"}", "ask_start"),
        (null, $"audio:{i}"),
        ("{\"type\":\"ask_extend\"}", "ask_extend"),
        ("{\"type\":\"ask_done\"}", "ask_done"),
        ("{\"type\":\"ask_cancel\"}", "ask_cancel"),
        ($"{{\"type\":\"trainer_mode\",\"on\":{(i % 2 == 0 ? "true" : "false")}}}", $"trainer_mode:{i % 2 == 0}"),
        ($"{{\"type\":\"train_turn\",\"question\":\"q{i}\",\"answer\":\"a{i}\",\"slideIndex\":{i % 3}}}", $"train_turn:q{i}:{i % 3}")
    ];

    /// <summary>Records every admitted call; each takes a little time so that overlapping calls would be observable.</summary>
    private sealed class RecordingPresenter : IPresenter
    {
        private readonly object _gate = new();
        private readonly List<string> _calls = [];
        private PresenterSnapshot _snapshot = new("presenting", "sample", "Sample", 0, 3, false, false, "s", null, 0, 200);
        private int _inFlight;
        private int _maxInFlight;
        private int _startedAfterEnd;
        private volatile bool _ended;

        public event Action<PresenterSnapshot>? State;
        public event Action<int>? Slide { add { } remove { } }
        public event Action<PresenterAudio>? Audio { add { } remove { } }
        public event Action<PresenterTranscript>? Transcript { add { } remove { } }
        public event Action<PresenterUsage>? Usage { add { } remove { } }
        public event Action<PresenterClosed>? Closed;
        public event Action<PresenterLog>? Log { add { } remove { } }
        public event Action<PresenterUpstreamError>? UpstreamError { add { } remove { } }

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (_gate) return [.. _calls];
            }
        }

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);
        public int CallsStartedAfterEnd => Volatile.Read(ref _startedAfterEnd);
        public bool Ended => _ended;

        public PresenterSnapshot Snapshot() => _snapshot;

        private async Task<bool> RecordAsync(string call)
        {
            var inFlight = Interlocked.Increment(ref _inFlight);
            int observed;
            while ((observed = Volatile.Read(ref _maxInFlight)) < inFlight &&
                Interlocked.CompareExchange(ref _maxInFlight, inFlight, observed) != observed)
            {
            }

            if (_ended) Interlocked.Increment(ref _startedAfterEnd);
            lock (_gate) _calls.Add(call);
            try
            {
                await Task.Delay(1).ConfigureAwait(false);
                return true;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public Task<PresenterStartResult> StartAsync(string id, int? fromIndex, string ownerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PresenterStartResult(false, id, null, null, null));

        public Task<bool> EndAsync(bool resumable = false, CancellationToken cancellationToken = default)
        {
            _ended = true;
            _snapshot = new("idle", null, null, 0, 0, false, false, null, null, 0, 200);
            Closed?.Invoke(new PresenterClosed("disconnect", 0));
            State?.Invoke(_snapshot);
            return Task.FromResult(true);
        }

        public Task<bool> SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken = default) =>
            RecordAsync($"audio:{BitConverter.ToInt32(pcm16.Span[..4]) - 1_000_000}");

        public Task<bool> NextAsync(CancellationToken cancellationToken = default) => RecordAsync("next");
        public Task<bool> PrevAsync(CancellationToken cancellationToken = default) => RecordAsync("prev");
        public Task<bool> GotoAsync(int index, CancellationToken cancellationToken = default) => RecordAsync($"goto:{index}");
        public Task<bool> PauseAsync(CancellationToken cancellationToken = default) => RecordAsync("pause");
        public Task<bool> ResumeAsync(CancellationToken cancellationToken = default) => RecordAsync("resume");
        public Task<bool> MuteAsync(CancellationToken cancellationToken = default) => RecordAsync("mute");
        public Task<bool> UnmuteAsync(CancellationToken cancellationToken = default) => RecordAsync("unmute");
        public Task<bool> AskStartAsync(CancellationToken cancellationToken = default) => RecordAsync("ask_start");
        public Task<bool> AskDoneAsync(CancellationToken cancellationToken = default) => RecordAsync("ask_done");
        public Task<bool> AskExtendAsync(CancellationToken cancellationToken = default) => RecordAsync("ask_extend");
        public Task<bool> AskCancelAsync(CancellationToken cancellationToken = default) => RecordAsync("ask_cancel");

        public Task<bool> SetTrainerModeAsync(string ownerId, bool on, CancellationToken cancellationToken = default) =>
            RecordAsync($"trainer_mode:{on}");

        public Task<bool> TrainOnTurnAsync(string ownerId, string question, string answer, int slideIndex,
            CancellationToken cancellationToken = default) => RecordAsync($"train_turn:{question}:{slideIndex}");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
