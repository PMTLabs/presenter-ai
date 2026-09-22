using PresenterAi.Application.Presenting;

namespace PresenterAi.Application.Content;

/// <summary>Ownerless Markdown input used by local CLI workflows.</summary>
public interface IPresentationImportSource
{
    Task<IReadOnlyList<PresentationListRow>> ListFilesAsync(CancellationToken cancellationToken = default);

    Task<LoadedPresentation> ReadAsync(string id, CancellationToken cancellationToken = default);
}
