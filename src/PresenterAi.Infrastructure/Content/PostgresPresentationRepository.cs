using Microsoft.EntityFrameworkCore;
using PresenterAi.Application.Content;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Scripts;
using PresenterAi.Infrastructure.Persistence;

namespace PresenterAi.Infrastructure.Content;

/// <summary>Owner-scoped presentation reads from the persisted Markdown representation.</summary>
public sealed class PostgresPresentationRepository(PresenterAiDbContext db) : IPresentationRepository
{
    public async Task<PresentationListResult> ListAsync(
        string ownerId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var query = db.Presentations
            .AsNoTracking()
            .Where(presentation => presentation.OwnerId == ownerId)
            .OrderBy(presentation => presentation.Slug);
        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var offset = (long)(page - 1) * pageSize;
        var skip = (int)Math.Min(offset, int.MaxValue);
        var items = await query
            .Skip(skip)
            .Take(pageSize)
            .Select(presentation => new PresentationListRow(
                presentation.Id,
                presentation.Title,
                presentation.SlideCount,
                presentation.Deck,
                presentation.Driver,
                null))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return new PresentationListResult(items, total);
    }

    public async Task<LoadedPresentation> LoadAsync(
        string ownerId,
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var presentation = await db.Presentations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.OwnerId == ownerId && candidate.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
        if (presentation is null)
        {
            throw new FileNotFoundException("The owner-scoped presentation was not found.", id);
        }

        var script = ScriptParser.Parse(presentation.Script, presentation.Id);
        return new LoadedPresentation(script.Meta.Id, script.Meta, script.Slides, presentation.Context);
    }
}
