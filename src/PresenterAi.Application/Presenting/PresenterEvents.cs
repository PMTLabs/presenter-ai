namespace PresenterAi.Application.Presenting;

public sealed record PresenterAudio(ReadOnlyMemory<byte> Bytes, long? StartMs, long? EndMs);

public sealed record PresenterTranscript(string Role, string Delta, long? StartMs, long? EndMs);

public sealed record PresenterUsage(double Seconds, double? Ratio);

public sealed record PresenterClosed(
    string Reason,
    double? Seconds,
    string EndReason = EndReasons.UpstreamLost,
    bool UsageConfirmed = false,
    double EstimatedSeconds = 0,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? EndedAt = null);

public sealed record PresenterLimitWarning(string Kind, int? SecondsLeft);

public sealed record PresenterUpstreamStatus(string Status);

public sealed record PresenterLog(string Level, string Message);

public sealed record PresenterUpstreamError(string Message, string? Code, string? ClientEventId = null);


/// <summary>An edit's status for the <c>script_edit</c> frame (plan 010 §4.3); values from <c>ScriptEditStatus</c>/<c>ScriptEditErrors</c>.</summary>
/// <param name="SlideIndexes">The edit's original target slide indexes.</param>
/// <param name="Version">Only when <c>applied</c>.</param>
/// <param name="Summary">Only when <c>applied</c>.</param>
/// <param name="Error">Only when <c>failed</c>.</param>
public sealed record PresenterScriptEdit(
    string Id,
    string Status,
    IReadOnlyList<int> SlideIndexes,
    int? Version,
    string? Summary,
    string? Error);

/// <summary>The talk's script version and trainer availability for the <c>script_version</c> frame (plan 010 §4.3).</summary>
public sealed record PresenterScriptVersion(
    string PresentationId,
    int Version,
    bool TrainerMode,
    bool TrainerAvailable,
    bool VoiceTraining);

/// <summary>
/// The server-authoritative Trainer mode for the <c>trainer_state</c> frame (additive to plan 010 §4.3): the running
/// talk's mode, or while no talk runs the stored request for the next Start. <paramref name="OwnerId"/> is whose talk or
/// request it is (null when neither); a bridge reports <paramref name="TrainerMode"/> only to that user.
/// <paramref name="VoiceTraining"/> describes the live connection and is true outside a talk.
/// </summary>
public sealed record PresenterTrainerState(
    string? OwnerId,
    bool TrainerMode,
    bool TrainerAvailable,
    bool VoiceTraining);

/// <summary>
/// One press-to-ask state for the <c>ask_state</c> frame (plan 011 §4.3). <paramref name="State"/> is
/// <c>listening</c>, <c>answering</c> or <c>off</c>; <paramref name="Reason"/> is null while listening, the send reason
/// on <c>answering</c> and the outcome on <c>off</c>. The remaining times are null outside <c>listening</c>.
/// </summary>
public sealed record PresenterAskState(
    string State,
    long ElapsedMs,
    long? QuietRemainingMs,
    long? SpeechRemainingMs,
    bool Heard,
    bool Transcribing,
    string? Reason);
