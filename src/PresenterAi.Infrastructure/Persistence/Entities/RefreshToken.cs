namespace PresenterAi.Infrastructure.Persistence.Entities;

public sealed class RefreshToken
{
    public RefreshToken() => Id = OpaqueIdGenerator.Create("rft");

    public string Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public bool IsRevoked { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public User User { get; set; } = null!;
}
