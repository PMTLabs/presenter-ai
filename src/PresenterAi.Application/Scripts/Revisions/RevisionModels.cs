namespace PresenterAi.Application.Scripts.Revisions;

/// <summary>
/// The durable head of a presentation: <c>presentations.version</c> plus the full Markdown of that version and its
/// parsed form. Returned by <see cref="IPresentationRevisionStore.GetHeadAsync"/>.
/// </summary>
public sealed record PresentationHead(string PresentationId, int Version, string Markdown, PresentationScript Script)
{
    public IReadOnlyList<Slide> Slides => Script.Slides;

    public HeadSnapshot ToSnapshot() => new(PresentationId, Version, Script.Slides);
}

/// <summary>A revision to append on top of the expected head version (plan 010 §4.3 columns).</summary>
/// <param name="Script">Full Markdown of the new version.</param>
/// <param name="Source">One of <see cref="RevisionSources"/>.</param>
/// <param name="Summary">One-line summary (stored as varchar(300)).</param>
/// <param name="BaseVersion">The head the change was applied on.</param>
/// <param name="RevertedFrom">The revision copied by a revert; null otherwise.</param>
/// <param name="ChangedSlides">0-based slide indexes whose narration changed.</param>
/// <param name="CreatedBy">User id; null for a backfill.</param>
public sealed record NewRevision(
    string Script,
    string Source,
    string Summary,
    int? BaseVersion,
    int? RevertedFrom,
    IReadOnlyList<int> ChangedSlides,
    string? CreatedBy);

/// <summary>A stored revision without its Markdown (list rows).</summary>
public sealed record RevisionInfo(
    int Number,
    string Source,
    DateTimeOffset CreatedAt,
    string Summary,
    int? BaseVersion,
    int? RevertedFrom,
    IReadOnlyList<int> ChangedSlides,
    string? CreatedBy);

/// <summary>A stored revision with the full Markdown of that version.</summary>
public sealed record RevisionRecord(RevisionInfo Info, string Script)
{
    public int Number => Info.Number;
}

/// <summary>One page of revisions, newest first, plus the current head version (for <c>isCurrent</c>).</summary>
public sealed record RevisionList(IReadOnlyList<RevisionInfo> Items, int Total, int CurrentVersion);

/// <summary>Result of <see cref="IPresentationRevisionStore.TryAppendAsync"/>.</summary>
public abstract record AppendResult
{
    private AppendResult()
    {
    }

    /// <summary>The CAS succeeded; <paramref name="Version"/> is the new head (= expected + 1).</summary>
    public sealed record Applied(int Version) : AppendResult;

    /// <summary>The head moved; nothing was written.</summary>
    public sealed record Conflict(int CurrentVersion) : AppendResult;

    /// <summary>No presentation with that id for that owner; nothing was written.</summary>
    public sealed record NotFound : AppendResult;
}
