using System.Text.Json.Serialization;

namespace PresenterAi.Contracts.Presentations;

public sealed record PresentationSummary(
    string Id,
    string Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SlideCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Deck,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Driver,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error);

public sealed record PresentationDetail(string Id, PresentationMetaDto Meta, IReadOnlyList<SlideDto> Slides, bool HasContext);

public sealed record PresentationMetaDto(
    string Id,
    string Title,
    string Deck,
    string Driver,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Voice,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Context,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? AdvanceSilenceMs,
    int ChunkChars,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxMinutes);

public sealed record SlideDto(int Index, int Number, string Title, string Narration, string? Notes);

public sealed record ConfigDto(string Model, string Voice, int AdvanceSilenceMs);
