using PresenterAi.Application.Presenting;

namespace PresenterAi.Application.Sessions;

/// <summary>Records one presenter run. Event handlers only enqueue and never perform persistence. Begin/End await
/// their persistence attempts; failures are logged and do not stop the live presentation.</summary>
public interface ISessionRecorder : IAsyncDisposable
{
    void Attach(IPresenter presenter);

    void Detach();

    Task BeginAsync(string userId, PresenterStartResult result, CancellationToken cancellationToken = default);

    Task EndAsync(string closeReason = "disconnect", double? seconds = null);
}

/// <summary>Creates a recorder whose lifetime is limited to one presenter run.</summary>
public interface ISessionRecorderFactory
{
    ISessionRecorder Create();
}
