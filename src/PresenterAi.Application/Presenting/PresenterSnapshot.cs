namespace PresenterAi.Application.Presenting;

/// <summary>The bridge state payload. Property names serialize to the frozen camel-case WebSocket shape.</summary>
public sealed record PresenterSnapshot(
    string State,
    string? PresentationId,
    string? Title,
    int SlideIndex,
    int SlideCount,
    bool Paused,
    bool Muted,
    string? SessionId,
    long? ExpiresAt,
    double UsageSeconds,
    int AdvanceSilenceMs);
