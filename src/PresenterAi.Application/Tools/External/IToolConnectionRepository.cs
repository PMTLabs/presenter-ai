namespace PresenterAi.Application.Tools.External;

public sealed record ToolConnection(Guid Id, string OwnerId, string Name, string Slug, string Url,
    string AuthKind, string Status, string? LastErrorCode, bool AlwaysAsk,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? LastConnectedAt,
    bool HasCredential = false);

public sealed record ToolCredential(byte[] Ciphertext, string KeyId, DateTimeOffset? AccessExpiresAt, uint Version);

public interface IToolConnectionRepository
{
    Task<IReadOnlyList<ToolConnection>> ListAsync(string ownerId, CancellationToken cancellationToken = default);
    Task<ToolConnection?> GetAsync(string ownerId, Guid serverId, CancellationToken cancellationToken = default);
    Task<ToolConnection> AddAsync(string ownerId, string name, string url, CancellationToken cancellationToken = default);
    Task<bool> UpdateAsync(string ownerId, Guid serverId, string? name, bool? alwaysAsk, CancellationToken cancellationToken = default);
    Task<bool> SetStatusAsync(string ownerId, Guid serverId, string status, string? errorCode, CancellationToken cancellationToken = default);
    Task<bool> SetStatusAsync(string ownerId, Guid serverId, string status, string? errorCode, string? authKind, CancellationToken cancellationToken = default);
    Task<bool> SetStatusIfCredentialVersionAsync(string ownerId, Guid serverId, uint? version, string status,
        string? errorCode, CancellationToken cancellationToken = default, string? authKind = null);
    Task<bool> RemoveAsync(string ownerId, Guid serverId, CancellationToken cancellationToken = default);
    Task<ToolCredential?> GetCredentialAsync(string ownerId, Guid serverId, CancellationToken cancellationToken = default);
    Task<bool> SaveCredentialAsync(string ownerId, Guid serverId, byte[] ciphertext, string keyId,
        DateTimeOffset? accessExpiresAt = null, uint? expectedVersion = null, CancellationToken cancellationToken = default);
    Task<bool> DisconnectAsync(string ownerId, Guid serverId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, bool>> GetOverridesAsync(string ownerId, Guid serverId, CancellationToken cancellationToken = default);
    Task<bool> SetOverrideAsync(string ownerId, Guid serverId, string toolName, bool alwaysAsk, CancellationToken cancellationToken = default);
    Task<bool> GetWebSearchEnabledAsync(string ownerId, CancellationToken cancellationToken = default);
    Task SetWebSearchEnabledAsync(string ownerId, bool enabled, CancellationToken cancellationToken = default);
}
