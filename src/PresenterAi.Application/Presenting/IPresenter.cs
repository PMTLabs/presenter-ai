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
    event Action<PresenterTrainerState>? TrainerState { add { } remove { } }

    /// <summary>Press-to-ask state changes for the <c>ask_state</c> frame (plan 011 §4.3).</summary>
    event Action<PresenterAskState>? AskState { add { } remove { } }

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

    /// <summary>
    /// The running talk's last <c>script_version</c> (plan 010), for a bridge that connects mid-talk; null when no talk runs.
    /// </summary>
    PresenterScriptVersion? CurrentScriptVersion() => null;

    /// <summary>
    /// The current Trainer mode (plan 010), in a talk or requested for the next Start, for a bridge that connects; raised
    /// again through <see cref="TrainerState"/> on every change.
    /// </summary>
    PresenterTrainerState CurrentTrainerState() => new(null, false, false, true);

    /// <summary>"Train on this": queues an edit of <paramref name="slideIndex"/> from a transcript exchange.</summary>
    Task<bool> TrainOnTurnAsync(
        string ownerId,
        string question,
        string answer,
        int slideIndex,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <summary>Press-to-ask (plan 011): starts listening; the answer arrives through <see cref="AskState"/>. False when refused.</summary>
    Task<bool> AskStartAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <summary>Press-to-ask (plan 011): finishes listening and sends the question; false when not listening.</summary>
    Task<bool> AskDoneAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <summary>Press-to-ask (plan 011): restarts the quiet timer; false when not listening.</summary>
    Task<bool> AskExtendAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <summary>Press-to-ask (plan 011): discards the question; false when not listening or awaiting the unmute ack.</summary>
    Task<bool> AskCancelAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
}
