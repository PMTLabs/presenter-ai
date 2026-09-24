using System.Text.Json;
using PresenterAi.Application.Presenting;

namespace PresenterAi.Application.Tests.Presenting;

internal sealed class FakeSession : ILiveSession
{
    public List<(string Type, string? Content, string? EventId, string? DelegationId)> Sent { get; } = [];

    public bool FailConnect { get; set; }

    public string DelegationMode { get; set; } = "responses";

    public bool ThrowOnClose { get; set; }
    public bool RefuseToolOutput { get; set; }
    public bool RefuseContinue { get; set; }
    public TaskCompletionSource? CloseGate { get; set; }
    public TaskCompletionSource? ConnectGate { get; set; }
    public double? CloseSeconds { get; set; } = 7;
    public bool ThrowOnAppend { get; set; }
    public bool DeferCloseEvent { get; set; }

    public string? WarnOnConnect { get; set; }

    /// <summary>Plan 011: the bytes of every accepted <see cref="SendAudio"/>, in the order of its <c>audio</c> entry in <see cref="Sent"/>.</summary>
    public List<byte[]> SentAudio { get; } = [];

    /// <summary>
    /// Plan 011 (P-12): the 0-based index of the first <see cref="SendAudio"/> call that is refused (returns false and
    /// records nothing); every later call is refused too. Null accepts every call.
    /// </summary>
    public int? RefuseAudioFromChunk { get; set; }

    public int SendAudioCalls { get; private set; }

    /// <summary>Plan 011 (P-18): when true, <see cref="Unmute"/> raises <see cref="InputAudioUnmuted"/> at once, as the upstream acks.</summary>
    public bool AutoAckUnmute { get; set; } = true;

    public bool RefuseUnmute { get; set; }

    public bool RefuseMute { get; set; }

    public SessionRequest? Request { get; set; }

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
    public event Action<string>? Warning;
    public event Action<string, string>? DelegatedResponseFinished;
    public event Action<string, string, string, string>? ToolCallRequested;
    public event Action<string, string, string>? HostedToolActivity;

    public void RaiseHostedActivity(string delegationId, string status) => HostedToolActivity?.Invoke(delegationId, "web_search", status);
    public event Action<string, double?>? Closed;
    public event Action? InputAudioUnmuted;
    public event Action<string, long>? InputPositionMarked;

    /// <summary>Raises the upstream's <c>session.input_audio.unmuted</c> ack (P-18), e.g. late or after a timeout.</summary>
    public void RaiseInputAudioUnmuted() => InputAudioUnmuted?.Invoke();

    /// <summary>Reports a mark queued by <see cref="MarkInputPosition"/> at <paramref name="sentMs"/> of the input clock.</summary>
    public void RaiseInputPositionMarked(string id, long sentMs) => InputPositionMarked?.Invoke(id, sentMs);

    public async Task<LiveSessionInfo> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (ConnectGate is not null) await ConnectGate.Task.WaitAsync(cancellationToken);
        if (FailConnect)
        {
            throw new LiveStartupException("invalid_model", Error("invalid_model", "startup error"), "startup error");
        }

        if (WarnOnConnect is not null)
        {
            Warning?.Invoke(WarnOnConnect);
        }

        State = LiveSessionState.Open;
        var session = new LiveSessionInfo("sess_test", "test", 123, Json("{\"id\":\"sess_test\",\"expires_at\":123}"), DelegationMode);
        Started?.Invoke(session);
        return session;
    }

    public string? AppendInstructions(string content, string? eventId = null, string? delegationId = null) => Append("instructions", content, eventId, delegationId);

    public string? AppendThinking(string content, string? eventId = null, string? delegationId = null) => Append("thinking", content, eventId, delegationId);

    public string? AppendCommentary(string content, string? eventId = null, string? delegationId = null) => Append("commentary", content, eventId, delegationId);

    public bool SubmitToolOutput(string callId, string output)
    {
        if (RefuseToolOutput) return false;
        Sent.Add(("tool_output", output, callId, null));
        return true;
    }

    public bool ContinueResponses()
    {
        if (RefuseContinue) return false;
        Sent.Add(("continue_responses", null, null, null));
        return true;
    }

    public bool Mute()
    {
        if (RefuseMute) return false;
        Sent.Add(("mute", null, null, null));
        return true;
    }

    public bool Unmute()
    {
        if (RefuseUnmute) return false;
        Sent.Add(("unmute", null, null, null));
        if (AutoAckUnmute) InputAudioUnmuted?.Invoke();
        return true;
    }

    public bool SendAudio(ReadOnlyMemory<byte> pcm16)
    {
        var call = SendAudioCalls++;
        if (RefuseAudioFromChunk is { } refuseFrom && call >= refuseFrom) return false;
        Sent.Add(("audio", null, null, null));
        SentAudio.Add(pcm16.ToArray());
        return true;
    }

    private int _marks;

    public string? MarkInputPosition()
    {
        var id = $"mark_{++_marks}";
        Sent.Add(("mark", null, id, null));
        return id;
    }

    public async Task<LiveCloseResult> CloseAsync()
    {
        Sent.Add(("close", null, null, null));
        if (CloseGate is not null) await CloseGate.Task;
        if (ThrowOnClose)
        {
            throw new InvalidOperationException("close failed");
        }

        if (!DeferCloseEvent)
        {
            State = LiveSessionState.Closed;
            Closed?.Invoke(CloseSeconds is null ? "close_timeout" : "close_requested", CloseSeconds);
        }
        return new LiveCloseResult(CloseSeconds is null ? "close_timeout" : "close_requested", CloseSeconds);
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

    public void Speak(int milliseconds = 100, long? startMs = 0, long? endMs = null) =>
        Audio?.Invoke(AudioLevelTests.VoicedFrame(milliseconds * 48), startMs, endMs ?? milliseconds);

    public void Silence(int milliseconds = 100) => Audio?.Invoke(new byte[milliseconds * 48], 0, milliseconds);

    public void Hear(string text = "hi", long? startMs = 0, long? endMs = 100) => Transcript?.Invoke("user", text, startMs, endMs);

    public void ModelTranscript(string text, long? startMs = 0, long? endMs = 100) => Transcript?.Invoke("assistant", text, startMs, endMs);

    public void Drop() => Closed?.Invoke("connection_lost", null);

    public void Close() => Closed?.Invoke("close_requested", 7);

    public void RaiseUsage(double seconds, double? ratio) => Usage?.Invoke(seconds, ratio);

    public void RaiseUpstreamError(string code, string message, string? clientEventId = null) =>
        UpstreamError?.Invoke(Error(code, message, clientEventId));

    public void RaiseWarning(string message) => Warning?.Invoke(message);

    public void RaiseDelegation(string target = "client", string id = "delegation_test") =>
        Delegation?.Invoke(Json($"{{\"type\":\"session.delegation.created\",\"offset_ms\":1000,\"delegation\":{{\"id\":\"{id}\",\"type\":\"delegation\",\"target\":\"{target}\"}}}}"));

    public void RaiseDelegatedResponse(string id, string type = "response.completed") =>
        DelegatedResponseFinished?.Invoke(id, type);

    public void RaiseToolCall(string delegationId, string callId, string name, string arguments) =>
        ToolCallRequested?.Invoke(delegationId, callId, name, arguments);

    private string? Append(string type, string content, string? eventId, string? delegationId)
    {
        if (ThrowOnAppend) throw new InvalidOperationException("append failed");
        Sent.Add((type, content, eventId, delegationId));
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
