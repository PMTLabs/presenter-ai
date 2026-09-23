namespace PresenterAi.Application.Tools.External;

public interface ISessionToolSource
{
    Task<SessionToolSet> LoadAsync(string ownerId, CancellationToken cancellationToken = default);
}
