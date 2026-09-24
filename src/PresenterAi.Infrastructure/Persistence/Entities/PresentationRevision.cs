namespace PresenterAi.Infrastructure.Persistence.Entities;

/// <summary>
/// One version of a presentation's script (plan 010 §4.3, table <c>presentation_revisions</c>). Append-only history;
/// <see cref="Presentation.Script"/>/<see cref="Presentation.Version"/> stay the durable head.
/// </summary>
public sealed class PresentationRevision
{
    public string PresentationId { get; set; } = string.Empty;
    public int Number { get; set; }
    public string Script { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public int? BaseVersion { get; set; }
    public int? RevertedFrom { get; set; }
    public int[] ChangedSlides { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }

    public Presentation Presentation { get; set; } = null!;
}
