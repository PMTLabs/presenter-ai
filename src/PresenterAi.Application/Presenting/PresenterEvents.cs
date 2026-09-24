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

