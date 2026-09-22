using PresenterAi.Application.Presenting;

namespace PresenterAi.Application.Content;

public sealed record PresentationListRow(string Id, string Title, int? SlideCount, string? Deck, string? Driver, string? Error);

public interface IPresentationRepository
{
    Task<IReadOnlyList<PresentationListRow>> ListAsync(CancellationToken cancellationToken = default);

    Task<LoadedPresentation> LoadAsync(string id, CancellationToken cancellationToken = default);
}
