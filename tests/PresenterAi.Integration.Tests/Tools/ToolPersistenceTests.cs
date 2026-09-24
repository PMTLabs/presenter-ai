using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PresenterAi.Infrastructure.Persistence;
using PresenterAi.Infrastructure.Persistence.Entities;
using PresenterAi.Infrastructure.Tools;
using PresenterAi.Integration.Tests.Support;
using Xunit;

namespace PresenterAi.Integration.Tests.Tools;

[Collection(IntegrationCollection.Name)]
public sealed class ToolPersistenceTests(PostgresFixture postgres)
{
    private PresenterAiDbContext Context() => new(new DbContextOptionsBuilder<PresenterAiDbContext>()
        .UseNpgsql(postgres.ConnectionString).Options);
    private static CredentialProtector Protector(byte[] key) => new(Options.Create(new ExternalToolsOptions
    {
        CredentialKey = Convert.ToBase64String(key)
    }));

    private static User User() => new()
    {
        Email = $"{Guid.NewGuid():N}@example.test",
        AuthMethod = "test",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task Owner_scope_and_limit_are_enforced_for_every_operation()
    {
        await using var db = Context();
        await db.Database.MigrateAsync();
        var a = User();
        var b = User();
        db.Users.AddRange(a, b);
        await db.SaveChangesAsync();
        var repo = new PostgresToolConnectionRepository(db);
        var server = await repo.AddAsync(a.Id, "Alpha Server", "https://example.org/mcp");
        (await repo.GetAsync(b.Id, server.Id)).Should().BeNull();
        (await repo.ListAsync(b.Id)).Should().BeEmpty();
        (await repo.UpdateAsync(b.Id, server.Id, "Intruder", true)).Should().BeFalse();
        (await repo.SetStatusAsync(b.Id, server.Id, "error", "auth")).Should().BeFalse();
        (await repo.SetOverrideAsync(b.Id, server.Id, "search", true)).Should().BeFalse();
        (await repo.GetOverridesAsync(b.Id, server.Id)).Should().BeEmpty();
        (await repo.DisconnectAsync(b.Id, server.Id)).Should().BeFalse();
        (await repo.RemoveAsync(b.Id, server.Id)).Should().BeFalse();
        (await repo.GetCredentialAsync(b.Id, server.Id)).Should().BeNull();
        (await repo.SaveCredentialAsync(b.Id, server.Id, [1], "test")).Should().BeFalse();
        (await repo.GetAsync(a.Id, server.Id))!.Name.Should().Be("Alpha Server");
        (await repo.GetWebSearchEnabledAsync(a.Id)).Should().BeFalse();
        await repo.SetWebSearchEnabledAsync(a.Id, true);
        (await repo.GetWebSearchEnabledAsync(a.Id)).Should().BeTrue();
        (await repo.GetWebSearchEnabledAsync(b.Id)).Should().BeFalse();
        for (var i = 1; i < 10; i++)
        {
            await repo.AddAsync(a.Id, "Alpha Server", "https://example.org/mcp");
        }
        (await repo.ListAsync(a.Id)).Select(row => row.Slug).Distinct().Should().HaveCount(10);
        var act = () => repo.AddAsync(a.Id, "Eleventh", "https://example.org/mcp");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("tools_server_limit");
        (await repo.ListAsync(b.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Ciphertext_is_bound_to_owner_and_server_and_disconnect_preserves_server()
    {
        await using var db = Context();
        await db.Database.MigrateAsync();
        var a = User();
        var b = User();
        db.Users.AddRange(a, b);
        await db.SaveChangesAsync();
        var repo = new PostgresToolConnectionRepository(db);
        var first = await repo.AddAsync(a.Id, "First", "https://example.org/a");
        var second = await repo.AddAsync(b.Id, "Second", "https://example.org/b");
        var third = await repo.AddAsync(a.Id, "Third", "https://example.org/c");
        var protector = Protector(RandomNumberGenerator.GetBytes(32));
        var payload = "{\"kind\":\"header\",\"value\":\"test-only-secret-marker\"}";
        var encrypted = protector.Protect(a.Id, first.Id, payload);
        (await repo.SaveCredentialAsync(a.Id, first.Id, encrypted.Ciphertext, encrypted.KeyId)).Should().BeTrue();
        var stored = await repo.GetCredentialAsync(a.Id, first.Id);
        stored.Should().NotBeNull();
        Encoding.UTF8.GetString(stored!.Ciphertext).Should().NotContain("test-only-secret-marker");
        (await protector.ReadAsync(repo, a.Id, first.Id)).Payload.Should().Be(payload);
        var tampered = (byte[])encrypted.Ciphertext.Clone();
        tampered[^1] ^= 1;
        (await repo.SaveCredentialAsync(a.Id, first.Id, tampered, encrypted.KeyId)).Should().BeTrue();
        (await protector.ReadAsync(repo, a.Id, first.Id)).ErrorCode.Should().Be("credential_unreadable");
        (await repo.GetAsync(a.Id, first.Id))!.Status.Should().Be("needs_reconnect");
        (await repo.SaveCredentialAsync(b.Id, second.Id, encrypted.Ciphertext, encrypted.KeyId)).Should().BeTrue();
        (await repo.SaveCredentialAsync(a.Id, third.Id, encrypted.Ciphertext, encrypted.KeyId)).Should().BeTrue();
        (await protector.ReadAsync(repo, b.Id, second.Id)).ErrorCode.Should().Be("credential_unreadable");
        (await repo.GetAsync(b.Id, second.Id))!.Status.Should().Be("needs_reconnect");
        (await protector.ReadAsync(repo, a.Id, third.Id)).ErrorCode.Should().Be("credential_unreadable");
        (await repo.GetAsync(a.Id, third.Id))!.Status.Should().Be("needs_reconnect");
        (await repo.GetCredentialAsync(b.Id, first.Id)).Should().BeNull();
        (await repo.DisconnectAsync(a.Id, first.Id)).Should().BeTrue();
        (await repo.GetCredentialAsync(a.Id, first.Id)).Should().BeNull();
        (await repo.GetAsync(a.Id, first.Id)).Should().NotBeNull();
        await repo.SetOverrideAsync(a.Id, third.Id, "search", true);
        (await repo.RemoveAsync(a.Id, third.Id)).Should().BeTrue();
        (await db.ToolServerCredentials.AsNoTracking().AnyAsync(row => row.ServerId == third.Id)).Should().BeFalse();
        (await db.ToolOverrides.AsNoTracking().AnyAsync(row => row.ServerId == third.Id)).Should().BeFalse();
        var newProtector = Protector(RandomNumberGenerator.GetBytes(32));
        (await newProtector.ReadAsync(repo, b.Id, second.Id)).ErrorCode.Should().Be("credential_key_changed");
        await repo.RemoveAsync(b.Id, second.Id);
    }

    [Fact]
    public async Task Changed_key_and_stale_version_do_not_overwrite_the_credential()
    {
        await using var db = Context();
        await db.Database.MigrateAsync();
        var user = User();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var repo = new PostgresToolConnectionRepository(db);
        var server = await repo.AddAsync(user.Id, "Versioned", "https://example.org");
        var original = Protector(RandomNumberGenerator.GetBytes(32));
        var frame = original.Protect(user.Id, server.Id, "test payload");
        await repo.SaveCredentialAsync(user.Id, server.Id, frame.Ciphertext, frame.KeyId);
        var credential = (await repo.GetCredentialAsync(user.Id, server.Id))!;
        (await repo.SaveCredentialAsync(user.Id, server.Id, frame.Ciphertext, frame.KeyId, null, credential.Version)).Should().BeTrue();
        (await repo.SaveCredentialAsync(user.Id, server.Id, [1], frame.KeyId, null, credential.Version)).Should().BeFalse();
        (await Protector(RandomNumberGenerator.GetBytes(32)).ReadAsync(repo, user.Id, server.Id)).ErrorCode
            .Should().Be("credential_key_changed");
        (await repo.GetAsync(user.Id, server.Id))!.LastErrorCode.Should().Be("credential_key_changed");
    }
}
