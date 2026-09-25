namespace PresenterAi.Application.Presenting.Asking;

/// <summary>Plan 011 §4.1 (G1-5): the default <see cref="IAskTranscriber"/>; transcription is built but not enabled.</summary>
public sealed class DisabledAskTranscriber : IAskTranscriber
{
    public IAskTranscription? Begin(string askId) => null;
}
