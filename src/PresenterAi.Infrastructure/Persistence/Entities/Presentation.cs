using System.Text.Json;

namespace PresenterAi.Infrastructure.Persistence.Entities;

public sealed class Presentation
{
    public Presentation() => Id = OpaqueIdGenerator.Create("prs");

    public string Id { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Deck { get; set; } = string.Empty;
    public string Driver { get; set; } = string.Empty;
    public string Script { get; set; } = string.Empty;
    public string? Context { get; set; }
    public JsonDocument? Frontmatter { get; set; }
    public int SlideCount { get; set; }
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public User Owner { get; set; } = null!;
    public ICollection<Session> Sessions { get; } = new List<Session>();
}
