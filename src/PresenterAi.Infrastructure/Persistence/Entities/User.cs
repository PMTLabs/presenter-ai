namespace PresenterAi.Infrastructure.Persistence.Entities;

public sealed class User
{
    public User() => Id = OpaqueIdGenerator.Create("usr");

    public string Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string Role { get; set; } = "user";
    public bool IsDisabled { get; set; }
    public string AuthMethod { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastSignInAt { get; set; }

    public ICollection<ExternalLogin> ExternalLogins { get; } = new List<ExternalLogin>();
    public ICollection<RefreshToken> RefreshTokens { get; } = new List<RefreshToken>();
    public ICollection<Presentation> Presentations { get; } = new List<Presentation>();
    public ICollection<Session> Sessions { get; } = new List<Session>();
}
