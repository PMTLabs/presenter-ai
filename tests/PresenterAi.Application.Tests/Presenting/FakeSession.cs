using System.Text.Json;
using PresenterAi.Application.Presenting;

namespace PresenterAi.Application.Tests.Presenting;

internal sealed class FakeSession : ILiveSession
{
    public List<(string Type, string? Content, string? EventId)> Sent { get; } = [];

    public bool FailConnect { get; set; }

    public bool ThrowOnClose { get; set; }

    public int DisposeCount { get; private set; }

    public LiveSessionState State { get; private set; } = LiveSessionState.Idle;

    public string? Id => "sess_test";

    public string? Name { get; set; }

    public long SilenceMs => 0;

    public event Action<LiveSessionInfo>? Started;
    public event Action<ReadOnlyMemory<byte>, long?, long?>? Audio;
    public event Action<string, string, long?, long?>? Transcript;
    public event Action<string, string?, JsonElement>? Appended;
    public event Action<double, double?>? Usage;
    public event Action<JsonElement>? Delegation;
    public event Action<JsonElement>? UpstreamError;
    public event Action<string, double?>? Closed;

    public Task<LiveSessionInfo> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (FailConnect)
        {
            throw new LiveStartupException("invalid_model", Error("invalid_model", "startup error"), "startup error");
        }

        State = LiveSessionState.Open;
        var session = new LiveSessionInfo("sess_test", "test", 123, Json("{\"id\":\"sess_test\",\"expires_at\":123}"));
        Started?.Invoke(session);
        return Task.FromResult(session);
    }

    public string? AppendInstructions(string content, string? eventId = null, string? delegationId = null) => Append("instructions", content, eventId);

    public string? AppendThinking(string content, string? eventId = null, string? delegationId = null) => Append("thinking", content, eventId);

    public string? AppendCommentary(string content, string? eventId = null, string? delegationId = null) => Append("commentary", content, eventId);

    public bool Mute()
    {
        Sent.Add(("mute", null, null));
        return true;
    }

    public bool Unmute()
    {
        Sent.Add(("unmute", null, null));
        return true;
    }

    public bool SendAudio(ReadOnlyMemory<byte> pcm16)
    {
        Sent.Add(("audio", null, null));
        return true;
    }

    public Task<LiveCloseResult> CloseAsync()
    {
        Sent.Add(("close", null, null));
        if (ThrowOnClose)
        {
            throw new InvalidOperationException("close failed");
        }

        State = LiveSessionState.Closed;
        Closed?.Invoke("close_requested", 7);
        return Task.FromResult(new LiveCloseResult("close_requested", 7));
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        State = LiveSessionState.Closed;
        return ValueTask.CompletedTask;
    }

    public void Terminate()
    {
        State = LiveSessionState.Closed;
    }

    public void Speak(int milliseconds = 100) => Audio?.Invoke(AudioLevelTests.VoicedFrame(milliseconds * 48), 0, milliseconds);

    public void Silence(int milliseconds = 100) => Audio?.Invoke(new byte[milliseconds * 48], 0, milliseconds);

    public void Hear(string text = "hi") => Transcript?.Invoke("user", text, 0, 100);

    public void Drop() => Closed?.Invoke("connection_lost", null);

    public void Close() => Closed?.Invoke("close_requested", 7);

    public void RaiseUsage(double seconds, double? ratio) => Usage?.Invoke(seconds, ratio);

    public void RaiseUpstreamError(string code, string message, string? clientEventId = null) =>
        UpstreamError?.Invoke(Error(code, message, clientEventId));

    public void RaiseDelegation() => Delegation?.Invoke(Json("{}"));

    private string? Append(string type, string content, string? eventId)
    {
        Sent.Add((type, content, eventId));
        Appended?.Invoke(type, eventId, Json("{}"));
        return eventId;
    }

    private static JsonElement Error(string code, string message, string? clientEventId = null) =>
        Json($"{{\"code\":\"{code}\",\"message\":\"{message}\"{(clientEventId is null ? string.Empty : $",\"client_event_id\":\"{clientEventId}\"")}}}");

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
