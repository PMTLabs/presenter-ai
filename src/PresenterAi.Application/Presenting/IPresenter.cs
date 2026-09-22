namespace PresenterAi.Application.Presenting;

public interface IPresenter : IAsyncDisposable
{
    event Action<PresenterSnapshot>? State;
    event Action<int>? Slide;
    event Action<PresenterAudio>? Audio;
    event Action<PresenterTranscript>? Transcript;
    event Action<PresenterUsage>? Usage;
    event Action<PresenterClosed>? Closed;
    event Action<PresenterLog>? Log;
    event Action<PresenterUpstreamError>? UpstreamError;

    PresenterSnapshot Snapshot();

    Task<bool> StartAsync(string id, int? fromIndex = null, CancellationToken cancellationToken = default);
    Task<bool> NextAsync(CancellationToken cancellationToken = default);
    Task<bool> PrevAsync(CancellationToken cancellationToken = default);
    Task<bool> GotoAsync(int index, CancellationToken cancellationToken = default);
    Task<bool> PauseAsync(CancellationToken cancellationToken = default);
    Task<bool> ResumeAsync(CancellationToken cancellationToken = default);
    Task<bool> MuteAsync(CancellationToken cancellationToken = default);
    Task<bool> UnmuteAsync(CancellationToken cancellationToken = default);
    Task<bool> SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken = default);
    Task<bool> EndAsync(CancellationToken cancellationToken = default);
}
