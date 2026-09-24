namespace PresenterAi.Application.Scripts.Revisions;

/// <summary>
/// Owns every script write except import (plan 010 §4.1): a FIFO worker per presentation that revises, validates,
/// composes and commits edits on the newest head, immediate reverts, the in-process head snapshots and per-edit
/// outcomes, and talk registrations that linearize End with commits. Implemented by <c>ScriptRevisionService</c>
/// (singleton, one async scope per store operation).
/// </summary>
public interface IScriptRevisionService
{
    /// <summary>
    /// Signal only (presentation id): raised after a head snapshot moved or an edit reached a terminal outcome (and on
    /// non-terminal progress). Receivers read state with <see cref="GetReconciliationSnapshot"/>; duplicated, late or
    /// reordered signals must be harmless.
    /// </summary>
    event Action<string>? Changed;

    /// <summary>True when the reviser has a route with a <c>DelegationModel</c> ("transcript training").</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Queues a confirmed edit of the talk <paramref name="talkId"/> and returns its id (<c>edit_&lt;n&gt;</c>) with
    /// outcome <c>queued</c>. Never blocks; a full queue makes the outcome terminal <c>failed/queue_full</c>.
    /// </summary>
    string Enqueue(string talkId, ScriptEditRequest request);

    /// <summary>
    /// Appends a copy of revision <paramref name="number"/> as the new head (<c>source=revert</c>), CAS with one retry on
    /// the new head. Not queued, no talk lock; cancelled by <paramref name="requestAborted"/> only.
    /// </summary>
    Task<RevertResult> RevertAsync(
        string ownerId,
        string presentationId,
        int number,
        string userId,
        CancellationToken requestAborted = default);

    /// <summary>Monotonic max: replaces the presentation's head snapshot only with a strictly newer version.</summary>
    void Observe(HeadSnapshot head);

    /// <summary>Head plus the outcomes of <paramref name="localEditIds"/>, read under one lock acquisition.</summary>
    ReconciliationSnapshot GetReconciliationSnapshot(string presentationId, IReadOnlyCollection<string> localEditIds);

    /// <summary>
    /// Registers a talk once its upstream is connected. Cancelling <paramref name="ticketToken"/> (the talk's
    /// <c>StartTicket</c>) closes the talk off-loop as <see cref="CloseTalkAsync"/> would.
    /// </summary>
    TalkRegistration OpenTalk(string talkId, string ownerId, string presentationId, CancellationToken ticketToken);

    /// <summary>
    /// Idempotent: waits up to 5 s for an in-flight commit of the talk, marks it closed (no commit of the talk starts
    /// afterwards, its reviser calls are cancelled) and prunes its outcomes. Unknown ids are a no-op.
    /// </summary>
    Task CloseTalkAsync(string talkId);
}
