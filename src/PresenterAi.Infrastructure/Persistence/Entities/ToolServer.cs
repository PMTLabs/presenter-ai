namespace PresenterAi.Infrastructure.Persistence.Entities;

public sealed class ToolServer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = string.Empty;
    public User Owner { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string AuthKind { get; set; } = "none";
    public string Status { get; set; } = "not_connected";
    public string? LastErrorCode { get; set; }
    public bool AlwaysAsk { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastConnectedAt { get; set; }
    public ToolServerCredential? Credential { get; set; }
    public ICollection<ToolOverride> Overrides { get; } = new List<ToolOverride>();
}

public sealed class ToolServerCredential
{
    public Guid ServerId { get; set; }
    public ToolServer Server { get; set; } = null!;
    public byte[] Ciphertext { get; set; } = [];
    public string KeyId { get; set; } = string.Empty;
    public DateTimeOffset? AccessExpiresAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint Version { get; set; }
}

public sealed class ToolOverride
{
    public Guid ServerId { get; set; }
    public ToolServer Server { get; set; } = null!;
    public string ToolName { get; set; } = string.Empty;
    public bool AlwaysAsk { get; set; }
}

public sealed class UserToolSettings
{
    public string UserId { get; set; } = string.Empty;
    public User User { get; set; } = null!;
    public bool WebSearchEnabled { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
