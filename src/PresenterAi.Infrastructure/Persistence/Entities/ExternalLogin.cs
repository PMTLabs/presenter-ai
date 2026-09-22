namespace PresenterAi.Infrastructure.Persistence.Entities;

public sealed class ExternalLogin
{
    public ExternalLogin() => Id = OpaqueIdGenerator.Create("ext");

    public string Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string ProviderEmail { get; set; } = string.Empty;
    public bool ProviderEmailVerified { get; set; }
    public string? ProviderDisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public User User { get; set; } = null!;
}
