namespace PresenterAi.Application.Scripts;

public sealed record PresentationScript(PresentationMeta Meta, IReadOnlyList<Slide> Slides);

public sealed record PresentationMeta(
    string Id,
    string Title,
    string Deck,
    string Driver,
    string? Voice,
    string? Context,
    int? AdvanceSilenceMs,
    int ChunkChars = 1400,
    int? MaxMinutes = null);

public sealed record Slide(int Index, int Number, string Title, string Narration, string? Notes);
