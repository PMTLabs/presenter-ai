namespace PresenterAi.Contracts.Presentations;

/// <summary>One script version in <c>GET /v1/presentations/{id}/revisions</c> (plan 010 §4.3).</summary>
/// <param name="Source"><c>import</c>, <c>live_edit</c> or <c>revert</c>.</param>
/// <param name="BaseVersion">The version the change was applied on; null for an initial import.</param>
/// <param name="RevertedFrom">The version a revert copied; null otherwise.</param>
/// <param name="ChangedSlides">0-based indexes of the slides whose narration changed.</param>
/// <param name="IsCurrent">True for the presentation's current version.</param>
public sealed record RevisionSummary(
    int Number,
    string Source,
    DateTimeOffset CreatedAt,
    string Summary,
    int? BaseVersion,
    int? RevertedFrom,
    IReadOnlyList<int> ChangedSlides,
    bool IsCurrent);

/// <summary>A version with every slide and the narration that changed against its base version.</summary>
public sealed record RevisionDetail(
    int Number,
    string Source,
    DateTimeOffset CreatedAt,
    string Summary,
    int? BaseVersion,
    int? RevertedFrom,
    IReadOnlyList<int> ChangedSlides,
    bool IsCurrent,
    IReadOnlyList<RevisionSlide> Slides,
    IReadOnlyList<RevisionChange> Changes);

public sealed record RevisionSlide(int Index, int Number, string Title, string Narration);

/// <summary>Narration of one slide before (in the base version) and after (in this version); null when absent.</summary>
public sealed record RevisionChange(int SlideIndex, string Title, string? Before, string? After);

/// <summary>An edit still queued or in flight when the revert committed; it will apply on top of the revert.</summary>
/// <param name="Status"><c>queued</c> or <c>processing</c>.</param>
public sealed record PendingEditDto(string Id, IReadOnlyList<int> SlideIndexes, string Status);

/// <summary>Body of <c>201</c> from <c>POST /v1/presentations/{id}/revisions/{number}/revert</c>.</summary>
public sealed record RevertResponse(RevisionSummary Revision, IReadOnlyList<PendingEditDto> PendingEdits);
