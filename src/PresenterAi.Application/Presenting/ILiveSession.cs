using System.Text.Json;

namespace PresenterAi.Application.Presenting;

public interface ILiveSession : IAsyncDisposable
{
    event Action<LiveSessionInfo>? Started;
    event Action<ReadOnlyMemory<byte>, long?, long?>? Audio;
    event Action<string, string, long?, long?>? Transcript;
    event Action<string, string?, JsonElement>? Appended;
    event Action<double, double?>? Usage;
    event Action<JsonElement>? Delegation;
    event Action<JsonElement>? UpstreamError;
    event Action<string>? Warning;
    event Action<string, string>? DelegatedResponseFinished;
    event Action<string, string, string, string>? ToolCallRequested;
    event Action<string, string, string>? HostedToolActivity;
    event Action<string, double?>? Closed;

    /// <summary>
    /// Plan 011 (P-13): raised by the send loop when it reaches a marker queued by <see cref="MarkInputPosition"/>,
    /// with the mark id and the ms of input audio appended upstream so far (pump silence included). Implementations
    /// without an input clock never raise it.
    /// </summary>
    event Action<string, long>? InputPositionMarked
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Plan 011 (P-18): raised when the upstream acknowledges <see cref="Unmute"/> with
    /// <c>session.input_audio.unmuted</c>, with the <c>client_event_id</c> it echoes (the event id
    /// <see cref="Unmute(out string?)"/> sent), or null when the ack carries none. Implementations without an ack never
    /// raise it.
    /// </summary>
    event Action<string?>? InputAudioUnmuted
    {
        add { }
        remove { }
    }

    LiveSessionState State { get; }

    string? Id { get; }

    string? Name { get; }

    long SilenceMs { get; }

    Task<LiveSessionInfo> ConnectAsync(CancellationToken cancellationToken = default);

    string? AppendInstructions(string content, string? eventId = null, string? delegationId = null);

    string? AppendThinking(string content, string? eventId = null, string? delegationId = null);

    string? AppendCommentary(string content, string? eventId = null, string? delegationId = null);

    bool SubmitToolOutput(string callId, string output);

    bool ContinueResponses();

    bool Mute();

    bool Unmute();

    /// <summary>
    /// Plan 011 (review r1 #1): <see cref="Unmute()"/> that also reports the event id it sent, so an ack can be
    /// attributed to it; null when the implementation sends no id.
    /// </summary>
    bool Unmute(out string? eventId)
    {
        eventId = null;
        return Unmute();
    }

    bool SendAudio(ReadOnlyMemory<byte> pcm16);

    /// <summary>
    /// Plan 011 (P-13): queues a marker in the outbound FIFO behind everything queued so far and returns its id, or
    /// null when the session cannot queue it. <see cref="InputPositionMarked"/> reports the input clock at the mark.
    /// </summary>
    string? MarkInputPosition() => null;

    Task<LiveCloseResult> CloseAsync();

    void Terminate();
}
