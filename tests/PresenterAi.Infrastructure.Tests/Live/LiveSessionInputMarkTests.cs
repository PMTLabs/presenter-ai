using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PresenterAi.Application.Presenting;
using PresenterAi.Infrastructure.Live;

namespace PresenterAi.Infrastructure.Tests.Live;

/// <summary>Plan 011 P-13: input-position marks travel through the outbound FIFO and report the upstream input clock.</summary>
public sealed class LiveSessionInputMarkTests
{
    private const int BytesPerMs = 48;

    [Fact]
    public async Task Marks_report_sent_ms_in_fifo_order_including_pump_silence()
    {
        await using var server = await FakeLiveServer.StartAsync();
        var clock = new FakeTimeProvider();
        await using var session = Create(server, clock);
        var marks = new ConcurrentQueue<(string Id, long SentMs)>();
        session.InputPositionMarked += (id, sentMs) => marks.Enqueue((id, sentMs));
        await session.ConnectAsync();
        await Task.Delay(20);

        // The pump fills (300 - 120 slack) / 20 = 9 frames of silence before the burst; they are on the input clock.
        clock.Advance(TimeSpan.FromMilliseconds(300));
        await EventuallyAsync(() => SilenceFrames(server) >= 8);
        await Task.Delay(50);
        var pumped = session.SilenceMs;
        pumped.Should().BeGreaterThan(0);
        var appendsBefore = AppendFrames(server);

        var startId = session.MarkInputPosition();
        var chunks = new[] { Voiced(200), Voiced(200), Voiced(200), new byte[1_000 * BytesPerMs] };
        foreach (var chunk in chunks)
        {
            session.SendAudio(chunk).Should().BeTrue();
        }

        var endId = session.MarkInputPosition();
        await EventuallyAsync(() => marks.Count == 2);

        startId.Should().NotBeNull();
        endId.Should().NotBeNull().And.NotBe(startId);
        var observed = marks.ToArray();
        observed.Select(mark => mark.Id).Should().Equal(startId, endId);
        observed[0].SentMs.Should().Be(pumped, "every pump-filled frame queued before the start mark is counted");
        observed[1].SentMs.Should().Be(observed[0].SentMs + 3 * 200 + 1_000, "the end mark follows the chunks and the zero tail");
        await EventuallyAsync(() => AppendFrames(server) == appendsBefore + chunks.Length);
    }

    [Fact]
    public async Task Mark_is_refused_before_the_session_is_open_and_after_it_closed()
    {
        await using var server = await FakeLiveServer.StartAsync();
        await using var session = Create(server, new FakeTimeProvider());
        var marks = 0;
        session.InputPositionMarked += (_, _) => Interlocked.Increment(ref marks);

        session.MarkInputPosition().Should().BeNull();
        await session.ConnectAsync();
        await session.CloseAsync();
        session.MarkInputPosition().Should().BeNull();
        marks.Should().Be(0);
    }

    [Fact]
    public void Implementations_without_an_input_clock_default_to_no_mark()
    {
        ILiveSession session = new ClocklessSession();
        session.InputPositionMarked += (_, _) => throw new InvalidOperationException("never raised");

        session.MarkInputPosition().Should().BeNull();
    }

    private static LiveSession Create(FakeLiveServer server, TimeProvider clock) =>
        new(
            new UpstreamRoute("azure-like", new Uri(server.Url), new Dictionary<string, string> { ["Authorization"] = "Bearer test" }, "test-model"),
            new LiveSessionConfig("test-model", "test instructions", "test-voice"),
            clock,
            NullLogger<LiveSession>.Instance,
            new LiveSessionOptions { CloseTimeout = TimeSpan.FromSeconds(5) });

    private static byte[] Voiced(int ms)
    {
        var bytes = new byte[ms * BytesPerMs];
        for (var index = 0; index < bytes.Length / 2; index++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(index * 2, 2), (short)(index % 2 == 0 ? 2_000 : -2_000));
        }

        return bytes;
    }

    private static int SilenceFrames(FakeLiveServer server) =>
        server.ReceivedSnapshot().Count(message => IsAppend(message) && message["silent"]!.GetValue<bool>());

    private static int AppendFrames(FakeLiveServer server) => server.ReceivedSnapshot().Count(IsAppend);

    private static bool IsAppend(JsonObject message) => message["type"]?.GetValue<string>() == "session.input_audio.append";

    private static async Task EventuallyAsync(Func<bool> condition, int timeoutMs = 2_000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for fake live server observation.");
    }

    private sealed class ClocklessSession : ILiveSession
    {
#pragma warning disable CS0067 // events of a stub that is never connected
        public event Action<LiveSessionInfo>? Started;
        public event Action<ReadOnlyMemory<byte>, long?, long?>? Audio;
        public event Action<string, string, long?, long?>? Transcript;
        public event Action<string, string?, System.Text.Json.JsonElement>? Appended;
        public event Action<double, double?>? Usage;
        public event Action<System.Text.Json.JsonElement>? Delegation;
        public event Action<System.Text.Json.JsonElement>? UpstreamError;
        public event Action<string>? Warning;
        public event Action<string, string>? DelegatedResponseFinished;
        public event Action<string, string, string, string>? ToolCallRequested;
        public event Action<string, string, string>? HostedToolActivity;
        public event Action<string, double?>? Closed;
#pragma warning restore CS0067

        public LiveSessionState State => LiveSessionState.Idle;
        public string? Id => null;
        public string? Name => null;
        public long SilenceMs => 0;
        public Task<LiveSessionInfo> ConnectAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public string? AppendInstructions(string content, string? eventId = null, string? delegationId = null) => null;
        public string? AppendThinking(string content, string? eventId = null, string? delegationId = null) => null;
        public string? AppendCommentary(string content, string? eventId = null, string? delegationId = null) => null;
        public bool SubmitToolOutput(string callId, string output) => false;
        public bool ContinueResponses() => false;
        public bool Mute() => false;
        public bool Unmute() => false;
        public bool SendAudio(ReadOnlyMemory<byte> pcm16) => false;
        public Task<LiveCloseResult> CloseAsync() => Task.FromResult(new LiveCloseResult("close_requested", null));
        public void Terminate() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
