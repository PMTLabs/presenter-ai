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
