namespace PresenterAi.Application.Presenting.Asking;

/// <summary>
/// Plan 011 §4.1: the cumulative text of one ask so far. <paramref name="Revision"/> strictly increases per ask, so the
/// presenter drops stale, reordered and duplicate updates; <paramref name="Final"/> marks an end of utterance.
/// </summary>
public sealed record AskTranscriptUpdate(long Revision, string Text, bool Final);
