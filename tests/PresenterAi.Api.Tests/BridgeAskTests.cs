using System.Net.WebSockets;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Api.Realtime;
using PresenterAi.Api.Tests.Infrastructure;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Presenting.Asking;
using PresenterAi.Infrastructure.Tests.Live;

namespace PresenterAi.Api.Tests;

/// <summary>Plan 011 T5: press-to-ask end to end over <c>/ws</c>, with real DI and only the upstream faked.</summary>
public sealed class BridgeAskTests
{
    [Fact]
    public async Task Ask_over_ws_mutes_records_and_bursts_in_order()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        fake.KeepAudio = true;
        fake.UnmuteAckGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var factory = AskSupport.Factory(fake);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await AskSupport.StartTalkAsync(socket);

        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_start\"}");
        var listening = await AskSupport.ReceiveAskStateAsync(socket, "listening");
        listening.Select(pair => pair.Key).Should().BeEquivalentTo(
            ["type", "state", "elapsedMs", "quietRemainingMs", "speechRemainingMs", "heard", "transcribing", "reason"]);
        listening["quietRemainingMs"]!.GetValue<long>().Should().Be(Presenter.AskQuietTimeoutMs);
        listening["transcribing"]!.GetValue<bool>().Should().BeFalse();
        listening["reason"].Should().BeNull();

        // Speech, a 1.2 s thinking pause (compressed), more speech.
        var frames = Enumerable.Range(0, 15).Select(AskSupport.VoicedFrame)
            .Concat(Enumerable.Repeat(0, 60).Select(_ => new byte[AskRecorder.WindowBytes]))
            .Concat(Enumerable.Range(100, 15).Select(AskSupport.VoicedFrame))
            .ToArray();
        foreach (var frame in frames) await AskSupport.SendBinaryAsync(socket, frame);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_extend\"}");
        (await AskSupport.ReceiveAskStateAsync(socket, "listening"))["heard"]!.GetValue<bool>().Should().BeTrue();

        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_done\"}");
        await BridgeTestSupport.WaitForAsync(() => AskSupport.IndexOf(fake.ReceivedSnapshot(), "session.input_audio.unmute") >= 0);
        await Task.Delay(300);
        AskSupport.BurstAppends(fake.ReceivedSnapshot()).Should().BeEmpty("the burst waits for the upstream's unmuted ack (P-18)");

        fake.UnmuteAckGate.TrySetResult();
        var answering = await AskSupport.ReceiveAskStateAsync(socket, "answering");
        answering["reason"]!.GetValue<string>().Should().Be("sent");
        var expected = AskSupport.ExpectedBurst(frames);
        await BridgeTestSupport.WaitForAsync(() =>
            AskSupport.BurstAppends(fake.ReceivedSnapshot()).Sum(append => append.Length) == expected.Length);

        var received = fake.ReceivedSnapshot();
        var mute = AskSupport.IndexOf(received, "session.input_audio.mute");
        var pause = received.ToList().FindIndex(message => message["type"]?.GetValue<string>() == "session.instructions.append" &&
            message["event_id"]?.GetValue<string>()?.StartsWith("pause-", StringComparison.Ordinal) == true);
        var unmute = AskSupport.IndexOf(received, "session.input_audio.unmute");
        mute.Should().BeGreaterThanOrEqualTo(0);
        pause.Should().BeGreaterThan(mute, "the upstream is muted before the ask's pause instruction");
        unmute.Should().BeGreaterThan(pause);
        received.Skip(mute).Take(unmute - mute)
            .Where(message => message["type"]?.GetValue<string>() == "session.input_audio.append")
            .Should().NotContain(message => !message["silent"]!.GetValue<bool>(), "no mic frame is forwarded while listening");
        var firstBurst = received.ToList().FindIndex(message => AskSupport.IsBurstAppend(message));
        firstBurst.Should().BeGreaterThan(unmute);
        AskSupport.BurstAppends(received).SelectMany(bytes => bytes).ToArray().Should().Equal(expected,
            "the appends after the unmute are the 200 ms zero lead-in and exactly the compressed recording, in order");
    }

    [Fact]
    public async Task End_while_the_burst_is_partly_on_the_wire_forwards_no_answer_after_closed()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        fake.GateReadsAfterBurstAppends = 2;
        using var factory = AskSupport.Factory(fake);
        var clock = factory.UseFakeClock();
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await AskSupport.StartTalkAsync(socket);
        await AskSupport.AskWithSpeechAsync(socket, voicedFrames: 50);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_done\"}");
        await AskSupport.ReceiveAskStateAsync(socket, "answering");
        await BridgeTestSupport.WaitForAsync(() => AskSupport.BurstAppends(fake.ReceivedSnapshot()).Count == 2);

        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"end\"}");
        using var answering = new CancellationTokenSource();
        var answers = Task.Run(async () =>
        {
            var sent = 0;
            while (!answering.IsCancellationRequested)
            {
                try
                {
                    await fake.SendEventAsync(AskSupport.AnswerAudioEvent(sent));
                    sent++;
                    await Task.Delay(10);
                }
                catch (Exception exception) when (exception is InvalidOperationException or WebSocketException)
                {
                    break;
                }
            }

            return sent;
        });

        // The close is queued behind chunks the upstream is not reading; its bounded close timeout runs on the clock.
        var frames = new List<(JsonObject? Text, byte[]? Binary)>();
        var closedAt = -1;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (closedAt < 0 && DateTimeOffset.UtcNow < deadline)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            closedAt = await AskSupport.CollectForAsync(socket, frames, TimeSpan.FromMilliseconds(200),
                frame => frame["type"]?.GetValue<string>() == "closed");
        }

        closedAt.Should().BeGreaterThanOrEqualTo(0, "End closes the talk");
        await AskSupport.CollectForAsync(socket, frames, TimeSpan.FromMilliseconds(500), _ => false);
        await answering.CancelAsync();
        (await answers).Should().BePositive("the upstream kept producing answer audio");
        fake.ReadGate.TrySetResult();

        frames.Skip(closedAt + 1).Where(frame => frame.Binary is not null).Should().BeEmpty("nothing is forwarded after closed");
        var offEnded = frames.FindIndex(frame => frame.Text?["type"]?.GetValue<string>() == "ask_state" &&
            frame.Text["state"]?.GetValue<string>() == "off");
        offEnded.Should().BeInRange(0, closedAt - 1, "off{ended} precedes closed");
        frames[offEnded].Text!["reason"]!.GetValue<string>().Should().Be("ended");
        frames[closedAt].Text!["endReason"]!.GetValue<string>().Should().Be(EndReasons.User);
    }

    [Fact]
    public async Task Upstream_socket_failure_after_the_burst_was_queued_ends_the_talk_upstream_lost()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = AskSupport.Factory(fake);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await AskSupport.StartTalkAsync(socket);
        await AskSupport.AskWithSpeechAsync(socket, voicedFrames: 20);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_done\"}");
        await AskSupport.ReceiveAskStateAsync(socket, "answering");
        await BridgeTestSupport.WaitForAsync(() => AskSupport.BurstAppends(fake.ReceivedSnapshot()).Count > 0);

        fake.DropAll();
        var frames = new List<(JsonObject? Text, byte[]? Binary)>();
        var closedAt = await AskSupport.CollectForAsync(socket, frames, TimeSpan.FromSeconds(5),
            frame => frame["type"]?.GetValue<string>() == "closed");

        closedAt.Should().BeGreaterThanOrEqualTo(0);
        frames[closedAt].Text!["endReason"]!.GetValue<string>().Should().Be(EndReasons.UpstreamLost);
        frames.Take(closedAt).Should().Contain(frame => frame.Text != null &&
            frame.Text["type"]!.GetValue<string>() == "ask_state" && frame.Text["state"]!.GetValue<string>() == "off" &&
            frame.Text["reason"]!.GetValue<string>() == "ended");
    }

    [Fact]
    public async Task Unmute_frame_while_listening_keeps_the_upstream_muted()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = AskSupport.Factory(fake);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await AskSupport.StartTalkAsync(socket);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_start\"}");
        await AskSupport.ReceiveAskStateAsync(socket, "listening");

        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"unmute\"}");
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_extend\"}");
        await AskSupport.ReceiveAskStateAsync(socket, "listening");
        // The upstream outbound is FIFO: once later pump frames arrive, an unmute queued before them would have too.
        var appends = AskSupport.Count(fake.ReceivedSnapshot(), "session.input_audio.append");
        await BridgeTestSupport.WaitForAsync(() =>
            AskSupport.Count(fake.ReceivedSnapshot(), "session.input_audio.append") >= appends + 3);

        var received = fake.ReceivedSnapshot();
        var mute = AskSupport.IndexOf(received, "session.input_audio.mute");
        mute.Should().BeGreaterThanOrEqualTo(0);
        received.Skip(mute).Should().NotContain(message =>
            message["type"]!.GetValue<string>() == "session.input_audio.unmute");
    }

    [Fact]
    public async Task Answer_audio_keeps_flowing_while_the_browser_sends_no_mic_frames()
    {
        const int answerFrames = 25;
        await using var fake = await FakeLiveServer.StartAsync();
        fake.PacedAnswerFrames = answerFrames;
        using var factory = AskSupport.Factory(fake);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await AskSupport.StartTalkAsync(socket);
        await AskSupport.AskWithSpeechAsync(socket, voicedFrames: 20);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_done\"}");

        // From here on the browser sends no mic frames; only the session's silence pump advances the input clock.
        var marker = FakeLiveServer.AnswerDelta();
        var frames = new List<(JsonObject? Text, byte[]? Binary)>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (frames.Count(frame => frame.Binary?.SequenceEqual(marker) == true) < answerFrames &&
            DateTimeOffset.UtcNow < deadline)
        {
            await AskSupport.CollectForAsync(socket, frames, TimeSpan.FromMilliseconds(200), _ => false);
        }

        frames.Count(frame => frame.Binary?.SequenceEqual(marker) == true).Should().Be(answerFrames,
            "every answer frame reaches the browser although it sends no mic audio (P-16)");
        var received = fake.ReceivedSnapshot().ToList();
        var lastBurst = received.FindLastIndex(message => AskSupport.IsBurstAppend(message));
        received.Skip(lastBurst + 1).Count(message => message["type"]!.GetValue<string>() == "session.input_audio.append" &&
            message["silent"]!.GetValue<bool>()).Should().BeGreaterThanOrEqualTo(answerFrames,
            "the pump's 960-byte silence frames keep the upstream input clock running");
        received.Skip(lastBurst + 1).Should().NotContain(message =>
            message["type"]!.GetValue<string>() == "session.input_audio.mute");
    }

    [Fact]
    public async Task Ask_commands_ignore_extra_fields_and_unknown_ask_type_is_protocol_error()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = AskSupport.Factory(fake);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await AskSupport.StartTalkAsync(socket);

        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_start\",\"extra\":1}");
        await AskSupport.ReceiveAskStateAsync(socket, "listening");
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_extend\",\"extra\":[true]}");
        (await AskSupport.ReceiveAskStateAsync(socket, "listening"))["quietRemainingMs"]!.GetValue<long>()
            .Should().Be(Presenter.AskQuietTimeoutMs);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_cancel\",\"extra\":{\"a\":\"b\"}}");
        (await AskSupport.ReceiveAskStateAsync(socket, "off"))["reason"]!.GetValue<string>().Should().Be("cancelled");
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_done\",\"extra\":null}");

        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_bogus\"}");
        var error = await BridgeTestSupport.ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "error");
        error["code"]!.GetValue<string>().Should().Be("protocol");
        error["message"]!.GetValue<string>().Should().Be("unknown command: ask_bogus");
    }

    [Fact]
    public async Task Refused_ask_start_while_muted_reports_refused_muted()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = AskSupport.Factory(fake);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await AskSupport.StartTalkAsync(socket);
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"mute\"}");
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_start\"}");

        var off = await AskSupport.ReceiveAskStateAsync(socket, "off");
        off["reason"]!.GetValue<string>().Should().Be("refused_muted");
        fake.ReceivedSnapshot().Count(message => message["type"]!.GetValue<string>() == "session.input_audio.mute")
            .Should().BeLessThanOrEqualTo(1, "only the user's mute reaches the upstream");
    }

    [Fact]
    public async Task Ask_done_when_not_asking_sends_no_frame()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        using var factory = AskSupport.Factory(fake);
        using var socket = await BridgeTestSupport.ConnectAsync(factory);
        await AskSupport.StartTalkAsync(socket);

        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_done\"}");
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_extend\"}");
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_cancel\"}");
        // Admission is serial: the pause's state frame follows the three ignored commands.
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"pause\"}");
        var frames = new List<(JsonObject? Text, byte[]? Binary)>();
        var paused = await AskSupport.CollectForAsync(socket, frames, TimeSpan.FromSeconds(5),
            frame => frame["type"]?.GetValue<string>() == "state" && frame["state"]?.GetValue<string>() == "paused");

        paused.Should().BeGreaterThanOrEqualTo(0);
        frames.Take(paused).Should().NotContain(frame => frame.Text != null &&
            frame.Text["type"]!.GetValue<string>() == "ask_state");
        AskSupport.BurstAppends(fake.ReceivedSnapshot()).Should().BeEmpty();
    }
}

/// <summary>Shared helpers for the plan 011 bridge tests.</summary>
internal static class AskSupport
{
    public static ApiFactory Factory(FakeLiveServer fake, params (string Key, string Value)[] extra)
    {
        var factory = BridgeTestSupport.Factory(fake);
        var overrides = new Dictionary<string, string?>(factory.Overrides!);
        foreach (var (key, value) in extra) overrides[key] = value;
        factory.Overrides = overrides;
        return factory;
    }

    public static async Task StartTalkAsync(WebSocket socket, string extra = "")
    {
        await BridgeTestSupport.SendAsync(socket, $"{{\"type\":\"start\",\"presentation\":\"sample\"{extra}}}");
        await BridgeTestSupport.ReceiveUntilAsync(socket, frame =>
            frame["type"]?.GetValue<string>() == "state" && frame["state"]?.GetValue<string>() == "presenting");
    }

    /// <summary>Starts an ask and records <paramref name="voicedFrames"/> distinct voiced frames; returns them.</summary>
    public static async Task<byte[][]> AskWithSpeechAsync(WebSocket socket, int voicedFrames, int firstSeed = 0)
    {
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_start\"}");
        await ReceiveAskStateAsync(socket, "listening");
        var frames = Enumerable.Range(firstSeed, voicedFrames).Select(VoicedFrame).ToArray();
        foreach (var frame in frames) await SendBinaryAsync(socket, frame);
        // Admission is serial, so this listening frame proves every frame above was recorded.
        await BridgeTestSupport.SendAsync(socket, "{\"type\":\"ask_extend\"}");
        await ReceiveAskStateAsync(socket, "listening");
        return frames;
    }

    public static Task<JsonObject> ReceiveAskStateAsync(WebSocket socket, string state) =>
        BridgeTestSupport.ReceiveUntilAsync(socket, frame =>
            frame["type"]?.GetValue<string>() == "ask_state" && frame["state"]?.GetValue<string>() == state);

    public static Task SendBinaryAsync(WebSocket socket, byte[] bytes) =>
        socket.SendAsync(bytes, WebSocketMessageType.Binary, true, CancellationToken.None);

    /// <summary>A distinct voiced 20 ms PCM16 frame (RMS well above the 120 threshold); the seed is in its first samples.</summary>
    public static byte[] VoicedFrame(int seed)
    {
        var bytes = new byte[AskRecorder.WindowBytes];
        for (var index = 0; index < bytes.Length / 2; index++)
        {
            var sample = (short)Math.Round(Math.Sin((index + seed * 3) / 4d) * 2500);
            BitConverter.TryWriteBytes(bytes.AsSpan(index * 2, 2), sample);
        }

        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), seed + 1_000_000);
        return bytes;
    }

    /// <summary>The 200 ms zero lead-in followed by what the presenter's recorder makes of <paramref name="frames"/>.</summary>
    public static byte[] ExpectedBurst(IEnumerable<byte[]> frames)
    {
        var recorder = new AskRecorder();
        foreach (var frame in frames) recorder.Append(frame);
        var stream = new List<byte>(new byte[Presenter.AskLeadInMs * AskRecorder.BytesPerMs]);
        foreach (var chunk in recorder.Complete()) stream.AddRange(chunk.ToArray());
        return [.. stream];
    }

    public static bool IsBurstAppend(JsonObject message) =>
        message["type"]?.GetValue<string>() == "session.input_audio.append" &&
        message["audioLength"]?.GetValue<int>() != Convert.ToBase64String(new byte[AskRecorder.WindowBytes]).Length;

    /// <summary>Appends that are not 960-byte frames: the lead-in and the burst chunks (needs <c>KeepAudio</c> for bytes).</summary>
    public static List<byte[]> BurstAppends(IReadOnlyList<JsonObject> received) =>
        received.Where(IsBurstAppend)
            .Select(message => message["audio"] is { } audio ? Convert.FromBase64String(audio.GetValue<string>()) : [])
            .ToList();

    public static int IndexOf(IReadOnlyList<JsonObject> received, string type) =>
        received.ToList().FindIndex(message => message["type"]?.GetValue<string>() == type);

    public static int Count(IReadOnlyList<JsonObject> received, string type) =>
        received.Count(message => message["type"]?.GetValue<string>() == type);

    public static JsonObject AnswerAudioEvent(int index) => new()
    {
        ["type"] = "session.output_audio.delta",
        ["delta"] = Convert.ToBase64String(FakeLiveServer.AnswerDelta()),
        ["start_ms"] = 90_000 + index * 20,
        ["end_ms"] = 90_000 + index * 20 + 20
    };

    /// <summary>
    /// Reads frames into <paramref name="frames"/> for up to <paramref name="window"/>; returns the index of the first
    /// text frame matching <paramref name="stop"/> (reading stops there), or -1.
    /// </summary>
    public static async Task<int> CollectForAsync(WebSocket socket, List<(JsonObject? Text, byte[]? Binary)> frames,
        TimeSpan window, Func<JsonObject, bool> stop)
    {
        var deadline = DateTimeOffset.UtcNow + window;
        while (DateTimeOffset.UtcNow < deadline && socket.State == WebSocketState.Open)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            var pending = _pending.TryGetValue(socket, out var read) ? read : ReceiveRawAsync(socket);
            var completed = await Task.WhenAny(pending, Task.Delay(remaining));
            if (completed != pending)
            {
                _pending[socket] = pending;
                break;
            }

            _pending.Remove(socket);
            var (text, binary) = await pending;
            if (text is null && binary is null) break;
            var json = text is null ? null : JsonNode.Parse(text)!.AsObject();
            frames.Add((json, binary));
            if (json is not null && stop(json)) return frames.Count - 1;
        }

        return -1;
    }

    // A receive that outlives one collection window is resumed by the next, so no frame is lost between windows.
    private static readonly PendingReads _pending = new();

    private static async Task<(string? Text, byte[]? Binary)> ReceiveRawAsync(WebSocket socket)
    {
        var buffer = new byte[32 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        try
        {
            do
            {
                result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) return (null, null);
                stream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);
        }
        catch (WebSocketException)
        {
            return (null, null);
        }

        return result.MessageType == WebSocketMessageType.Binary
            ? (null, stream.ToArray())
            : (System.Text.Encoding.UTF8.GetString(stream.ToArray()), null);
    }

    private sealed class PendingReads
    {
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<WebSocket, Task<(string? Text, byte[]? Binary)>> _reads = new();

        public bool TryGetValue(WebSocket socket, out Task<(string? Text, byte[]? Binary)> read)
        {
            lock (_reads)
            {
                return _reads.TryGetValue(socket, out read!);
            }
        }

        public Task<(string? Text, byte[]? Binary)> this[WebSocket socket]
        {
            set
            {
                lock (_reads) _reads.AddOrUpdate(socket, value);
            }
        }

        public void Remove(WebSocket socket)
        {
            lock (_reads) _reads.Remove(socket);
        }
    }
}

/// <summary>Holds the admission pump before each item while closed (plan 011 T5 seam); cancelled when admission stops.</summary>
internal sealed class AdmissionGate
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _closed;

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static AdmissionGate Install(ApiFactory factory)
    {
        var gate = new AdmissionGate();
        factory.Services.GetRequiredService<PresenterBridge>().ConfigureAdmissionForTest(gate.BeforeItemAsync);
        return gate;
    }

    public void Close() => _closed = true;

    public void Release()
    {
        _closed = false;
        _release.TrySetResult();
    }

    private Task BeforeItemAsync(CancellationToken cancellationToken)
    {
        if (!_closed) return Task.CompletedTask;
        Entered.TrySetResult();
        return _release.Task.WaitAsync(cancellationToken);
    }
}
