namespace PresenterAi.Infrastructure.Persistence.Entities;

public sealed class Session
{
    public Session() => Id = OpaqueIdGenerator.Create("ses");

    public string Id { get; set; }
    public string PresentationId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int UsageSeconds { get; set; }
    public string Upstream { get; set; } = string.Empty;
    public string? UpstreamSessionId { get; set; }
    public string? CloseReason { get; set; }
    public string? EndReason { get; set; }
    public bool? UsageConfirmed { get; set; }
    public int? EstimatedSeconds { get; set; }

    public Presentation Presentation { get; set; } = null!;
    public User User { get; set; } = null!;
    public ICollection<SessionTurn> Turns { get; } = new List<SessionTurn>();
}
