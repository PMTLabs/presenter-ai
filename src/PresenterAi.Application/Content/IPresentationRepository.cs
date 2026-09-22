using PresenterAi.Application.Presenting;

namespace PresenterAi.Application.Content;

public sealed record PresentationListRow(string Id, string Title, int? SlideCount, string? Deck, string? Driver, string? Error);

public sealed record PresentationListResult(IReadOnlyList<PresentationListRow> Items, int Total);

public interface IPresentationRepository
{
    Task<PresentationListResult> ListAsync(
        string ownerId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<LoadedPresentation> LoadAsync(string ownerId, string id, CancellationToken cancellationToken = default);
}
