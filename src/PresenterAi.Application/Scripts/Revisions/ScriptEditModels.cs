namespace PresenterAi.Application.Scripts.Revisions;

/// <summary><c>script_edit.status</c> values (plan 010 §4.3).</summary>
public static class ScriptEditStatus
{
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Applied = "applied";
    public const string Failed = "failed";

    public static bool IsTerminal(string status) => status is Applied or Failed;
}

/// <summary><c>script_edit.error</c> values (plan 010 §4.3).</summary>
public static class ScriptEditErrors
{
    public const string Timeout = "timeout";
    public const string Upstream = "upstream";
    public const string InvalidOutput = "invalid_output";
    public const string Conflict = "conflict";
    public const string Cancelled = "cancelled";
    public const string QueueFull = "queue_full";
    public const string TrainerModeOff = "trainer_mode_off";
    public const string NotPresenting = "not_presenting";
}

/// <summary>
/// A confirmed edit handed to <see cref="IScriptRevisionService.Enqueue"/> by the presenter loop. The targets are
/// resolved when the edit was confirmed and never substituted later; the edit is applied on the newest head at
/// processing time, <see cref="BaseVersion"/> only records what the speaker saw.
/// </summary>
/// <param name="TargetSlideIndexes">0-based slide indexes to rewrite.</param>
/// <param name="Feedback">The confirmed request, in the speaker's words.</param>
/// <param name="Exchange">The Q&amp;A selected with "Train on this", if any.</param>
/// <param name="Recent">Recent transcript turns (untrusted context only).</param>
public sealed record ScriptEditRequest(
    string PresentationId,
    string OwnerId,
    IReadOnlyList<int> TargetSlideIndexes,
    int BaseVersion,
    string Feedback,
    TrainingExchange? Exchange,
    IReadOnlyList<RecentTurn> Recent);

/// <summary>
/// State of one edit. Non-terminal (<c>queued</c>/<c>processing</c>) or terminal <c>applied{version, summary}</c> /
/// <c>failed{error}</c>; a terminal outcome is written once and never changed.
/// </summary>
/// <param name="SlideIndexes">The edit's original target slide indexes.</param>
public sealed record EditOutcome(
    string Status,
    IReadOnlyList<int> SlideIndexes,
    int? Version = null,
    string? Summary = null,
    string? Error = null)
{
    public bool IsTerminal => ScriptEditStatus.IsTerminal(Status);

    public static EditOutcome Queued(IReadOnlyList<int> slideIndexes) => new(ScriptEditStatus.Queued, slideIndexes);

    public static EditOutcome Processing(IReadOnlyList<int> slideIndexes) => new(ScriptEditStatus.Processing, slideIndexes);

    public static EditOutcome Applied(IReadOnlyList<int> slideIndexes, int version, string summary) =>
        new(ScriptEditStatus.Applied, slideIndexes, version, summary);

    public static EditOutcome Failed(IReadOnlyList<int> slideIndexes, string error) =>
        new(ScriptEditStatus.Failed, slideIndexes, Error: error);
}

/// <summary>The service's in-process head of a presentation: version plus the full parsed slides.</summary>
public sealed record HeadSnapshot(string PresentationId, int Version, IReadOnlyList<Slide> Slides);

/// <summary>
/// One atomic read for the presenter's reconcile: the head and the outcomes of the requested edits, taken under a single
/// acquisition of the service lock. <see cref="Head"/> is null when the service has never observed the presentation;
/// ids the service no longer knows are absent from <see cref="Outcomes"/>.
/// </summary>
public sealed record ReconciliationSnapshot(HeadSnapshot? Head, IReadOnlyDictionary<string, EditOutcome> Outcomes);

/// <summary>An edit still queued or in flight when a revert committed.</summary>
public sealed record PendingEdit(string Id, IReadOnlyList<int> SlideIndexes, string Status);

public abstract record RevertResult
{
    private RevertResult()
    {
    }

    /// <summary>The revert committed as <paramref name="Revision"/>; the listed edits will still apply on top of it.</summary>
    public sealed record Reverted(RevisionInfo Revision, IReadOnlyList<PendingEdit> PendingEdits) : RevertResult;

    /// <summary>Lost the CAS twice (HTTP 409 <c>concurrency.conflict</c>).</summary>
    public sealed record Conflict : RevertResult;

    /// <summary>No presentation with that id for that owner (HTTP 404 <c>presentation.not_found</c>).</summary>
    public sealed record PresentationNotFound : RevertResult;

    /// <summary>The presentation exists but has no such revision (HTTP 404 <c>revision.not_found</c>).</summary>
    public sealed record RevisionNotFound : RevertResult;
}

/// <summary>
/// A talk's registration with the revision service (plan 010 §4.1 "Talk registration"). <see cref="CommitLock"/>
/// linearizes End with commits; closure is a one-shot <see cref="MarkClosed"/> that cancels <see cref="Cts"/> (reviser
/// calls and a transaction not yet at <c>COMMIT</c>) and never touches the lock. Disposal of <see cref="Cts"/>, the ticket
/// callback and the lock (reference counted, never while held) belongs to the service.
/// </summary>
public sealed class TalkRegistration
{
    private int _closed;

    public TalkRegistration(string talkId, string ownerId, string presentationId, CancellationToken lifetimeToken)
    {
        ArgumentNullException.ThrowIfNull(talkId);
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(presentationId);
        TalkId = talkId;
        OwnerId = ownerId;
        PresentationId = presentationId;
        Cts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
    }

    public string TalkId { get; }

    public string OwnerId { get; }

    public string PresentationId { get; }

    /// <summary>Held by a worker for its whole commit phase and by <c>CloseTalkAsync</c> (bounded wait).</summary>
    public SemaphoreSlim CommitLock { get; } = new(1, 1);

    /// <summary>Linked to the service lifetime; cancelled once by the caller that closes the talk.</summary>
    public CancellationTokenSource Cts { get; }

    public bool IsClosed => Volatile.Read(ref _closed) == 1;

    /// <summary>
    /// Atomic, idempotent closure: only the caller that flips the flag 0 → 1 cancels <see cref="Cts"/> and gets true.
    /// </summary>
    public bool MarkClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return false;
        }

        try
        {
            Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        return true;
    }
}
