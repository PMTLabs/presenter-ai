namespace PresenterAi.Application.Presenting.Asking;

/// <summary>
/// Plan 011 §4.1 (G1-5): the pluggable transcriber for the audio of one ask. The default registration,
/// <see cref="DisabledAskTranscriber"/>, returns null, so production has no live ask transcript and no spoken
/// "ask done"; a real transcriber comes in a later pass.
/// </summary>
public interface IAskTranscriber
{
    /// <summary>Starts transcribing the ask <paramref name="askId"/>; null when transcription is disabled.</summary>
    IAskTranscription? Begin(string askId);
}

/// <summary>One ask's transcription, owned by the presenter from Ask start until it is disposed at Ask done or cancel.</summary>
public interface IAskTranscription : IDisposable
{
    /// <summary>Called on the presenter loop with each recorded mic frame: copy and return; never block.</summary>
    void Append(ReadOnlySpan<byte> pcm16);

    /// <summary>Raised on any thread with the cumulative text of this ask; revisions strictly increase per ask.</summary>
    event Action<AskTranscriptUpdate>? Updated;
}
