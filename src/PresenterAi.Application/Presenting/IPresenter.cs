namespace PresenterAi.Application.Presenting;

public interface IPresenter : IAsyncDisposable
{
    event Action<PresenterSnapshot>? State;
    event Action<int>? Slide;
    event Action<PresenterAudio>? Audio;
    event Action? Flush { add { } remove { } }
    event Action<PresenterTranscript>? Transcript;
    event Action<PresenterUsage>? Usage;
    event Action<PresenterClosed>? Closed;
    event Action<PresenterLog>? Log;
    event Action<PresenterUpstreamError>? UpstreamError;
    event Action<PresenterLimitWarning>? LimitWarning { add { } remove { } }
    event Action<PresenterUpstreamStatus>? UpstreamStatus { add { } remove { } }
    event Action<PresenterScriptEdit>? ScriptEdit { add { } remove { } }
    event Action<PresenterScriptVersion>? ScriptVersion { add { } remove { } }

    PresenterSnapshot Snapshot();

    Task<PresenterStartResult> StartAsync(string id, int? fromIndex, string ownerId, CancellationToken cancellationToken = default);
    Task<PresenterStartResult> StartAsync(string id, int? fromIndex, string ownerId, int? maxMinutes, CancellationToken cancellationToken = default) =>
        StartAsync(id, fromIndex, ownerId, cancellationToken);
    Task<bool> NextAsync(CancellationToken cancellationToken = default);
    Task<bool> PrevAsync(CancellationToken cancellationToken = default);
    Task<bool> GotoAsync(int index, CancellationToken cancellationToken = default);
    Task<bool> PauseAsync(CancellationToken cancellationToken = default);
    Task<bool> RequestEndConfirmationAsync(bool confirmed, CancellationToken cancellationToken = default) => PauseAsync(cancellationToken);
    Task<bool> ResumeAsync(CancellationToken cancellationToken = default);
    Task<bool> MuteAsync(CancellationToken cancellationToken = default);
    Task<bool> UnmuteAsync(CancellationToken cancellationToken = default);
    Task<bool> SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken = default);
    Task<bool> EndAsync(bool resumable = false, CancellationToken cancellationToken = default);
    Task<bool> EndAsync(string endReason, bool resumable = false, CancellationToken cancellationToken = default) =>
        EndAsync(resumable, cancellationToken);
    void AbortPendingStart() { }

    /// <summary>Turns Trainer mode on or off for <paramref name="ownerId"/>'s talk (plan 010); false when refused.</summary>
    Task<bool> SetTrainerModeAsync(string ownerId, bool on, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <summary>"Train on this": queues an edit of <paramref name="slideIndex"/> from a transcript exchange.</summary>
    Task<bool> TrainOnTurnAsync(
        string ownerId,
        string question,
        string answer,
        int slideIndex,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
