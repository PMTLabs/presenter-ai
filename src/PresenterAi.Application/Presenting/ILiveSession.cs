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
    event Action<string, double?>? Closed;

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

    bool SendAudio(ReadOnlyMemory<byte> pcm16);

    Task<LiveCloseResult> CloseAsync();

    void Terminate();
}
