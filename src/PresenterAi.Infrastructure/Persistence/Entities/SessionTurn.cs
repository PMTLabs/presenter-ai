namespace PresenterAi.Infrastructure.Persistence.Entities;

public sealed class SessionTurn
{
    public SessionTurn() => Id = OpaqueIdGenerator.Create("trn");

    public string Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public int Ordinal { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public int? SlideNo { get; set; }
    public DateTimeOffset At { get; set; }

    public Session Session { get; set; } = null!;
}
