using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using PresenterAi.Application.Tools.External;

namespace PresenterAi.Api.Tests.Infrastructure;

public sealed class TestToolConnectionRepository : IToolConnectionRepository
{
    private readonly ConcurrentDictionary<Guid, ToolConnection> _connections = new();
    private readonly ConcurrentDictionary<Guid, ToolCredential> _credentials = new();
    private readonly ConcurrentDictionary<(Guid ServerId, string ToolName), bool> _overrides = new();
    private readonly ConcurrentDictionary<string, bool> _webSearchEnabled = new();

    public void Seed(ToolConnection connection, ToolCredential? credential = null)
    {
        _connections[connection.Id] = connection;
        if (credential != null)
        {
            _credentials[connection.Id] = credential;
        }
    }

    public void Clear()
    {
        _connections.Clear();
        _credentials.Clear();
        _overrides.Clear();
        _webSearchEnabled.Clear();
    }

    public Task<IReadOnlyList<ToolConnection>> ListAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        var list = _connections.Values
            .Where(c => c.OwnerId == ownerId)
            .OrderBy(c => c.CreatedAt)
            .Select(c => c with { HasCredential = _credentials.ContainsKey(c.Id) })
            .ToList();
        return Task.FromResult<IReadOnlyList<ToolConnection>>(list);
    }

    public Task<ToolConnection?> GetAsync(string ownerId, Guid serverId, CancellationToken cancellationToken = default)
    {
        if (_connections.TryGetValue(serverId, out var conn) && conn.OwnerId == ownerId)
        {
            return Task.FromResult<ToolConnection?>(conn with { HasCredential = _credentials.ContainsKey(serverId) });
        }
        return Task.FromResult<ToolConnection?>(null);
    }

    public Task<ToolConnection> AddAsync(string ownerId, string name, string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (name.Length is < 1 or > 40 || url.Length is < 1 or > 2048)
            throw new ArgumentOutOfRangeException(nameof(name), "Invalid server name or URL length");

        var currentCount = _connections.Values.Count(c => c.OwnerId == ownerId);
        if (currentCount >= 10)
            throw new InvalidOperationException("tools_server_limit");

        var baseSlug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (baseSlug.Length == 0) baseSlug = "server";
        baseSlug = baseSlug[..Math.Min(16, baseSlug.Length)].TrimEnd('-');

        var existingSlugs = _connections.Values.Where(c => c.OwnerId == ownerId).Select(c => c.Slug).ToHashSet();
        var slug = baseSlug;
        for (var suffix = 2; existingSlugs.Contains(slug); suffix++)
        {
            var end = $"-{suffix}";
            slug = baseSlug[..Math.Min(baseSlug.Length, 16 - end.Length)].TrimEnd('-') + end;
        }

        var now = DateTimeOffset.UtcNow;
        var conn = new ToolConnection(Guid.NewGuid(), ownerId, name, slug, url, "none", "connected", null, false, now, now, null, false);
        _connections[conn.Id] = conn;
        return Task.FromResult(conn);
    }

    public Task<bool> UpdateAsync(string ownerId, Guid serverId, string? name, bool? alwaysAsk, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(serverId, out var conn) || conn.OwnerId != ownerId)
            return Task.FromResult(false);

        var updated = conn with
        {
            Name = name ?? conn.Name,
            AlwaysAsk = alwaysAsk ?? conn.AlwaysAsk,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _connections[serverId] = updated;
        return Task.FromResult(true);
    }

    public Task<bool> SetStatusAsync(string ownerId, Guid serverId, string status, string? errorCode, CancellationToken cancellationToken = default) =>
        SetStatusAsync(ownerId, serverId, status, errorCode, null, cancellationToken);

    public Task<bool> SetStatusAsync(string ownerId, Guid serverId, string status, string? errorCode, string? authKind, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(serverId, out var conn) || conn.OwnerId != ownerId)
            return Task.FromResult(false);

        var updated = conn with
        {
            Status = status,
            LastErrorCode = errorCode,
            AuthKind = authKind ?? conn.AuthKind,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastConnectedAt = status == "connected" ? DateTimeOffset.UtcNow : conn.LastConnectedAt
        };
        _connections[serverId] = updated;
        return Task.FromResult(true);
    }

    public Task<bool> RemoveAsync(string ownerId, Guid serverId, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(serverId, out var conn) || conn.OwnerId != ownerId)
            return Task.FromResult(false);

        _connections.TryRemove(serverId, out _);
        _credentials.TryRemove(serverId, out _);
        var keysToRemove = _overrides.Keys.Where(k => k.ServerId == serverId).ToList();
        foreach (var key in keysToRemove)
        {
            _overrides.TryRemove(key, out _);
        }
        return Task.FromResult(true);
    }

    public Task<ToolCredential?> GetCredentialAsync(string ownerId, Guid serverId, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(serverId, out var conn) || conn.OwnerId != ownerId)
            return Task.FromResult<ToolCredential?>(null);

        _credentials.TryGetValue(serverId, out var cred);
        return Task.FromResult(cred);
    }

    public Task<bool> SaveCredentialAsync(string ownerId, Guid serverId, byte[] ciphertext, string keyId,
        DateTimeOffset? accessExpiresAt = null, uint? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(serverId, out var conn) || conn.OwnerId != ownerId)
            return Task.FromResult(false);

        var currentVersion = _credentials.TryGetValue(serverId, out var existing) ? existing.Version : 0;
        if (expectedVersion.HasValue && expectedVersion.Value != currentVersion)
            return Task.FromResult(false);

        var credential = new ToolCredential(ciphertext, keyId, accessExpiresAt, currentVersion + 1);
        _credentials[serverId] = credential;
        return Task.FromResult(true);
    }

    public Task<bool> DisconnectAsync(string ownerId, Guid serverId, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(serverId, out var conn) || conn.OwnerId != ownerId)
            return Task.FromResult(false);

        _credentials.TryRemove(serverId, out _);
        _connections[serverId] = conn with { AuthKind = "none", Status = "not_connected", UpdatedAt = DateTimeOffset.UtcNow };
        return Task.FromResult(true);
    }

    public Task<IReadOnlyDictionary<string, bool>> GetOverridesAsync(string ownerId, Guid serverId, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(serverId, out var conn) || conn.OwnerId != ownerId)
            return Task.FromResult<IReadOnlyDictionary<string, bool>>(new Dictionary<string, bool>());

        var result = _overrides
            .Where(kvp => kvp.Key.ServerId == serverId)
            .ToDictionary(kvp => kvp.Key.ToolName, kvp => kvp.Value);
        return Task.FromResult<IReadOnlyDictionary<string, bool>>(result);
    }

    public Task<bool> SetOverrideAsync(string ownerId, Guid serverId, string toolName, bool alwaysAsk, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(serverId, out var conn) || conn.OwnerId != ownerId)
            return Task.FromResult(false);

        _overrides[(serverId, toolName)] = alwaysAsk;
        return Task.FromResult(true);
    }

    public Task<bool> GetWebSearchEnabledAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        _webSearchEnabled.TryGetValue(ownerId, out var enabled);
        return Task.FromResult(enabled);
    }

    public Task SetWebSearchEnabledAsync(string ownerId, bool enabled, CancellationToken cancellationToken = default)
    {
        _webSearchEnabled[ownerId] = enabled;
        return Task.CompletedTask;
    }
}
