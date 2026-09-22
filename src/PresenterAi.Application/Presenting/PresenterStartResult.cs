namespace PresenterAi.Application.Presenting;

public sealed record PresenterStartResult(
    bool Started,
    string PresentationId,
    string? Upstream,
    string? UpstreamSessionId,
    string? Model);
