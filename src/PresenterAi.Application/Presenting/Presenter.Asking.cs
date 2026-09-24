using PresenterAi.Application.Presenting.Asking;

namespace PresenterAi.Application.Presenting;

/// <summary>
/// Plan 011 press-to-ask. T2 fixes the contracts only: the four ask commands are routed onto the loop like every other
/// command and are refused (<c>false</c>) there with no state change and no frame; T4 adds the ask exchange.
/// </summary>
public sealed partial class Presenter
{
    private readonly IAskTranscriber _askTranscriber;

    /// <summary>The transcriber the ask exchange will use (plan 011 G1-5); <see cref="DisabledAskTranscriber"/> by default.</summary>
    public IAskTranscriber AskTranscriber => _askTranscriber;

    public Task<bool> AskStartAsync(CancellationToken cancellationToken = default) =>
        EnqueueCommandAsync(new AskStartCommand(), cancellationToken);

    public Task<bool> AskDoneAsync(CancellationToken cancellationToken = default) =>
        EnqueueCommandAsync(new AskDoneCommand(), cancellationToken);

    public Task<bool> AskExtendAsync(CancellationToken cancellationToken = default) =>
        EnqueueCommandAsync(new AskExtendCommand(), cancellationToken);

    public Task<bool> AskCancelAsync(CancellationToken cancellationToken = default) =>
        EnqueueCommandAsync(new AskCancelCommand(), cancellationToken);

    private abstract record AskCommand : Command;
    private sealed record AskStartCommand : AskCommand;
    private sealed record AskDoneCommand : AskCommand;
    private sealed record AskExtendCommand : AskCommand;
    private sealed record AskCancelCommand : AskCommand;
}
