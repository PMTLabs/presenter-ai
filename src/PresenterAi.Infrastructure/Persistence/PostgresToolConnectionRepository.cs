using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PresenterAi.Application.Tools.External;
using PresenterAi.Infrastructure.Persistence.Entities;

namespace PresenterAi.Infrastructure.Persistence;

public sealed class PostgresToolConnectionRepository(PresenterAiDbContext db) : IToolConnectionRepository
{
    private IQueryable<ToolServer> Owned(string ownerId) => db.ToolServers.Where(server => server.OwnerId == ownerId);
    private static ToolConnection View(ToolServer server) => new(server.Id, server.OwnerId, server.Name,
        server.Slug, server.Url, server.AuthKind, server.Status, server.LastErrorCode, server.AlwaysAsk,
        server.CreatedAt, server.UpdatedAt, server.LastConnectedAt);

    public async Task<IReadOnlyList<ToolConnection>> ListAsync(string ownerId, CancellationToken cancellationToken = default) =>
        (await Owned(ownerId).AsNoTracking().OrderBy(server => server.CreatedAt).ToListAsync(cancellationToken))
        .Select(View).ToArray();

    public async Task<ToolConnection?> GetAsync(string ownerId, Guid serverId, CancellationToken cancellationToken = default)
    {
        var server = await Owned(ownerId).AsNoTracking().FirstOrDefaultAsync(server => server.Id == serverId, cancellationToken);
        return server is null ? null : View(server);
    }

    public async Task<ToolConnection> AddAsync(string ownerId, string name, string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (name.Length is < 1 or > 40 || url.Length is < 1 or > 2048)
            throw new ArgumentOutOfRangeException(nameof(name), "Invalid server name or URL length");
        // Serialise concurrent inserts for this owner, including the count check and slug allocation.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Users.Where(user => user.Id == ownerId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.UpdatedAt, user => user.UpdatedAt), cancellationToken);
        var slugs = await Owned(ownerId).Select(server => server.Slug).ToListAsync(cancellationToken);
        if (slugs.Count >= 10)
            throw new InvalidOperationException("tools_server_limit");
        var baseSlug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (baseSlug.Length == 0) baseSlug = "server";
        baseSlug = baseSlug[..Math.Min(16, baseSlug.Length)].TrimEnd('-');
        var slug = baseSlug;
        for (var suffix = 2; slugs.Contains(slug, StringComparer.Ordinal); suffix++)
        {
            var end = $"-{suffix}";
            slug = baseSlug[..Math.Min(baseSlug.Length, 16 - end.Length)].TrimEnd('-') + end;
        }
        var now = DateTimeOffset.UtcNow;
        var row = new ToolServer { OwnerId = ownerId, Name = name, Slug = slug, Url = url, CreatedAt = now, UpdatedAt = now };
        db.ToolServers.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return View(row);
    }

    public async Task<bool> UpdateAsync(string ownerId, Guid serverId, string? name, bool? alwaysAsk, CancellationToken cancellationToken = default)
    {
        if (name is not null && name.Length is < 1 or > 40) throw new ArgumentOutOfRangeException(nameof(name));
        var row = await Owned(ownerId).FirstOrDefaultAsync(server => server.Id == serverId, cancellationToken);
        if (row is null) return false;
        if (name is not null) row.Name = name;
        if (alwaysAsk.HasValue) row.AlwaysAsk = alwaysAsk.Value;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SetStatusAsync(string ownerId, Guid serverId, string status, string? errorCode, CancellationToken cancellationToken = default)
    {
        var row = await Owned(ownerId).FirstOrDefaultAsync(server => server.Id == serverId, cancellationToken);
        if (row is null) return false;
        row.Status = status;
        row.LastErrorCode = errorCode;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        if (status == "connected") row.LastConnectedAt = row.UpdatedAt;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RemoveAsync(string ownerId, Guid serverId, CancellationToken cancellationToken = default) =>
        await Owned(ownerId).Where(server => server.Id == serverId).ExecuteDeleteAsync(cancellationToken) != 0;

    public async Task<ToolCredential?> GetCredentialAsync(string ownerId, Guid serverId, CancellationToken cancellationToken = default)
    {
        var row = await db.ToolServerCredentials.AsNoTracking()
            .Where(credential => credential.ServerId == serverId && credential.Server.OwnerId == ownerId)
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : new ToolCredential(row.Ciphertext, row.KeyId, row.AccessExpiresAt, row.Version);
    }

    public async Task<bool> SaveCredentialAsync(string ownerId, Guid serverId, byte[] ciphertext, string keyId,
        DateTimeOffset? accessExpiresAt = null, uint? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        if (!await Owned(ownerId).AnyAsync(server => server.Id == serverId, cancellationToken)) return false;
        var row = await db.ToolServerCredentials.FirstOrDefaultAsync(credential => credential.ServerId == serverId && credential.Server.OwnerId == ownerId, cancellationToken);
        if (expectedVersion.HasValue && (row is null || row.Version != expectedVersion.Value)) return false;
        if (row is null)
        {
            row = new ToolServerCredential { ServerId = serverId };
            db.ToolServerCredentials.Add(row);
        }
        row.Ciphertext = ciphertext;
        row.KeyId = keyId;
        row.AccessExpiresAt = accessExpiresAt;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { db.Entry(row).State = EntityState.Detached; return false; }
        return true;
    }

    public async Task<bool> DisconnectAsync(string ownerId, Guid serverId, CancellationToken cancellationToken = default)
    {
        var server = await Owned(ownerId).FirstOrDefaultAsync(row => row.Id == serverId, cancellationToken);
        if (server is null) return false;
        await db.ToolServerCredentials.Where(row => row.ServerId == serverId).ExecuteDeleteAsync(cancellationToken);
        server.Status = "not_connected";
        server.AuthKind = "none";
        server.LastErrorCode = null;
        server.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyDictionary<string, bool>> GetOverridesAsync(string ownerId, Guid serverId, CancellationToken cancellationToken = default) =>
        await db.ToolOverrides.AsNoTracking().Where(row => row.ServerId == serverId && row.Server.OwnerId == ownerId)
            .ToDictionaryAsync(row => row.ToolName, row => row.AlwaysAsk, cancellationToken);

    public async Task<bool> SetOverrideAsync(string ownerId, Guid serverId, string toolName, bool alwaysAsk, CancellationToken cancellationToken = default)
    {
        if (toolName.Length is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(toolName));
        if (!await Owned(ownerId).AnyAsync(server => server.Id == serverId, cancellationToken)) return false;
        var row = await db.ToolOverrides.FindAsync([serverId, toolName], cancellationToken);
        if (row is null) db.ToolOverrides.Add(new ToolOverride { ServerId = serverId, ToolName = toolName, AlwaysAsk = alwaysAsk });
        else row.AlwaysAsk = alwaysAsk;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> GetWebSearchEnabledAsync(string ownerId, CancellationToken cancellationToken = default) =>
        await db.UserToolSettings.Where(row => row.UserId == ownerId)
            .Select(row => (bool?)row.WebSearchEnabled).FirstOrDefaultAsync(cancellationToken) ?? false;

    public async Task SetWebSearchEnabledAsync(string ownerId, bool enabled, CancellationToken cancellationToken = default)
    {
        var row = await db.UserToolSettings.FindAsync([ownerId], cancellationToken);
        if (row is null) db.UserToolSettings.Add(new UserToolSettings { UserId = ownerId, WebSearchEnabled = enabled, UpdatedAt = DateTimeOffset.UtcNow });
        else { row.WebSearchEnabled = enabled; row.UpdatedAt = DateTimeOffset.UtcNow; }
        await db.SaveChangesAsync(cancellationToken);
    }
}
