using PresenterAi.Application.Presenting;

namespace PresenterAi.Application.Content;

/// <summary>Raw Markdown together with its parsed, ownerless presentation.</summary>
public sealed record PresentationSource(string Markdown, LoadedPresentation Presentation);

/// <summary>Ownerless Markdown input used by local CLI workflows.</summary>
public interface IPresentationImportSource
{
    Task<IReadOnlyList<PresentationListRow>> ListFilesAsync(CancellationToken cancellationToken = default);

    Task<LoadedPresentation> ReadAsync(string id, CancellationToken cancellationToken = default);

    Task<PresentationSource> ReadSourceAsync(string id, CancellationToken cancellationToken = default);
}
