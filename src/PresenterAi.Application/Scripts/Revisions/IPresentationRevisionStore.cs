namespace PresenterAi.Application.Scripts.Revisions;

/// <summary>
/// Owner-scoped version history of a presentation (plan 010 §4.1). <c>presentations.script</c>/<c>version</c> stay
/// the durable head; revisions are the append-only history. Another owner's presentation is indistinguishable from a
/// missing one. Implementations: <c>PostgresPresentationRevisionStore</c> (scoped) and
/// <see cref="InMemoryPresentationRevisionStore"/> (singleton).
/// </summary>
public interface IPresentationRevisionStore
{
    /// <summary>The current head, or null when the presentation does not exist for this owner.</summary>
    Task<PresentationHead?> GetHeadAsync(string ownerId, string presentationId, CancellationToken cancellationToken = default);

    /// <summary>Revisions newest first (page is 1-based), or null when the presentation does not exist for this owner.</summary>
    Task<RevisionList?> ListAsync(
        string ownerId,
        string presentationId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revision <paramref name="number"/>, or null when either the presentation (for this owner) or the revision does not
    /// exist; callers that must tell the two apart check <see cref="GetHeadAsync"/> first.
    /// </summary>
    Task<RevisionRecord?> GetAsync(string ownerId, string presentationId, int number, CancellationToken cancellationToken = default);

    /// <summary>
    /// One transaction: compare-and-set the head version <paramref name="expectedVersion"/> → expected + 1 with the new
    /// script and insert the revision row numbered expected + 1. The token cancels it before <c>COMMIT</c> (throws
    /// <see cref="OperationCanceledException"/>, nothing written).
    /// </summary>
    Task<AppendResult> TryAppendAsync(
        string ownerId,
        string presentationId,
        int expectedVersion,
        NewRevision revision,
        CancellationToken cancellationToken = default);
}
