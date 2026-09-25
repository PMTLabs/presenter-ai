using Microsoft.EntityFrameworkCore;
using PresenterAi.Application.Scripts;
using PresenterAi.Application.Scripts.Revisions;
using PresenterAi.Infrastructure.Persistence;
using PresenterAi.Infrastructure.Persistence.Entities;

namespace PresenterAi.Infrastructure.Content;

/// <summary>
/// Owner-scoped revision history in Postgres (plan 010 §4.1). Scoped: one <see cref="PresenterAiDbContext"/> per
/// scope, so callers that outlive a request (the revision service) open a scope per operation. The head stays in
/// <c>presentations.script</c>/<c>version</c>; an append is one transaction that compare-and-sets the version and
/// inserts the revision row.
/// </summary>
public sealed class PostgresPresentationRevisionStore(PresenterAiDbContext db, TimeProvider timeProvider)
    : IPresentationRevisionStore
{
    public async Task<PresentationHead?> GetHeadAsync(
        string ownerId,
        string presentationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(presentationId);

        var row = await OwnedPresentations(ownerId, presentationId)
            .Select(presentation => new { presentation.Version, presentation.Script })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? null
            : new PresentationHead(presentationId, row.Version, row.Script, ScriptParser.Parse(row.Script, presentationId));
    }

    public async Task<RevisionList?> ListAsync(
        string ownerId,
        string presentationId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(presentationId);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var currentVersion = await OwnedPresentations(ownerId, presentationId)
            .Select(presentation => (int?)presentation.Version)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (currentVersion is null)
        {
            return null;
        }

        var query = db.PresentationRevisions.AsNoTracking().Where(revision => revision.PresentationId == presentationId);
        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var skip = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);
        var rows = await query
            .OrderByDescending(revision => revision.Number)
            .Skip(skip)
            .Take(pageSize)
            .Select(revision => new
            {
                revision.Number,
                revision.Source,
                revision.CreatedAt,
                revision.Summary,
                revision.BaseVersion,
                revision.RevertedFrom,
                revision.ChangedSlides,
                revision.CreatedBy
            })
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var items = rows
            .Select(row => new RevisionInfo(
                row.Number, row.Source, row.CreatedAt, row.Summary, row.BaseVersion, row.RevertedFrom, row.ChangedSlides, row.CreatedBy))
            .ToArray();
        return new RevisionList(items, total, currentVersion.Value);
    }

    public async Task<RevisionRecord?> GetAsync(
        string ownerId,
        string presentationId,
        int number,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(presentationId);

        var row = await db.PresentationRevisions
            .AsNoTracking()
            .Where(revision => revision.PresentationId == presentationId
                && revision.Number == number
                && revision.Presentation.OwnerId == ownerId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToRecord(row);
    }

    public async Task<AppendResult> TryAppendAsync(
        string ownerId,
        string presentationId,
        int expectedVersion,
        NewRevision revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(presentationId);
        ArgumentNullException.ThrowIfNull(revision);
        if (!RevisionSources.IsKnown(revision.Source))
        {
            throw new ArgumentException($"Unknown revision source '{revision.Source}'.", nameof(revision));
        }

        // Parsing rejects a malformed script before anything is written; the slide count follows the script (a revert
        // may cross an import that changed it). Title/deck/driver stay as imported (their fallbacks depend on the slug).
        var script = ScriptParser.Parse(revision.Script, presentationId);
        var number = expectedVersion + 1;

        // The context enables retry on transient failures, which requires user transactions to run inside the
        // execution strategy. A retried attempt starts clean and re-runs the whole CAS.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            async token =>
            {
                db.ChangeTracker.Clear();
                var now = timeProvider.GetUtcNow();
                await using var transaction = await db.Database.BeginTransactionAsync(token).ConfigureAwait(false);

                // The UPDATE row lock serialises concurrent writers; a writer that loses re-evaluates the WHERE on the
                // committed row and matches nothing.
                var updated = await OwnedPresentations(ownerId, presentationId)
                    .Where(presentation => presentation.Version == expectedVersion)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(presentation => presentation.Version, number)
                            .SetProperty(presentation => presentation.Script, revision.Script)
                            .SetProperty(presentation => presentation.SlideCount, script.Slides.Count)
                            .SetProperty(presentation => presentation.UpdatedAt, now),
                        token)
                    .ConfigureAwait(false);
                if (updated == 0)
                {
                    var current = await OwnedPresentations(ownerId, presentationId)
                        .Select(presentation => (int?)presentation.Version)
                        .SingleOrDefaultAsync(token)
                        .ConfigureAwait(false);
                    await transaction.RollbackAsync(token).ConfigureAwait(false);
                    return current is null ? (AppendResult)new AppendResult.NotFound() : new AppendResult.Conflict(current.Value);
                }

                db.PresentationRevisions.Add(new PresentationRevision
                {
                    PresentationId = presentationId,
                    Number = number,
                    Script = revision.Script,
                    Source = revision.Source,
                    Summary = revision.Summary,
                    BaseVersion = revision.BaseVersion,
                    RevertedFrom = revision.RevertedFrom,
                    ChangedSlides = revision.ChangedSlides.ToArray(),
                    CreatedAt = now,
                    CreatedBy = revision.CreatedBy
                });
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                return new AppendResult.Applied(number);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private IQueryable<Presentation> OwnedPresentations(string ownerId, string presentationId) =>
        db.Presentations.AsNoTracking().Where(presentation => presentation.OwnerId == ownerId && presentation.Id == presentationId);

    private static RevisionRecord ToRecord(PresentationRevision row) => new(
        new RevisionInfo(
            row.Number, row.Source, row.CreatedAt, row.Summary, row.BaseVersion, row.RevertedFrom, row.ChangedSlides, row.CreatedBy),
        row.Script);
}
