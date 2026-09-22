using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PresenterAi.Api.Auth;
using PresenterAi.Infrastructure.Persistence;
using PresenterAi.Infrastructure.Persistence.Entities;
using PresenterAi.Integration.Tests.Support;
using Xunit;

namespace PresenterAi.Integration.Tests.Auth;

[Collection(IntegrationCollection.Name)]
public sealed class RefreshTokenIntegrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Two_concurrent_redemptions_issue_exactly_one_successor_and_replay_is_rejected()
    {
        await using var setup = CreateContext();
        await setup.Database.MigrateAsync();
        var user = NewUser();
        setup.Users.Add(user);
        await setup.SaveChangesAsync();

        var settings = Options.Create(new JwtSettings
        {
            Issuer = "https://integration.presenter-ai.test",
            Audience = "presenter-ai-integration",
            SecretKey = "integration-only-jwt-signing-key-not-a-credential-123456",
            AccessTokenMinutes = 60,
            RefreshTokenDays = 30
        });
        var issuer = new TokenService(setup, settings, TimeProvider.System, NullLogger<TokenService>.Instance);
        var initial = await issuer.IssueAsync(user);

        await using var firstContext = CreateContext();
        await using var secondContext = CreateContext();
        var first = new TokenService(firstContext, settings, TimeProvider.System, NullLogger<TokenService>.Instance);
        var second = new TokenService(secondContext, settings, TimeProvider.System, NullLogger<TokenService>.Instance);

        var results = await Task.WhenAll(
            RedeemAsync(first, initial.RefreshToken),
            RedeemAsync(second, initial.RefreshToken));

        results.Count(result => result.Success).Should().Be(1);
        results.Count(result => !result.Success && result.Exception is AuthFailureException failure
            && failure.Code == PresenterAi.Contracts.ErrorCodes.AuthRequired).Should().Be(1);

        await using var verify = CreateContext();
        var tokens = await verify.RefreshTokens.Where(token => token.UserId == user.Id).ToListAsync();
        tokens.Should().HaveCount(2);
        tokens.Count(token => token.IsRevoked).Should().Be(1);

        var replay = new TokenService(CreateContext(), settings, TimeProvider.System, NullLogger<TokenService>.Instance);
        var replayAttempt = async () => await replay.RotateAsync(initial.RefreshToken);
        await replayAttempt.Should().ThrowAsync<AuthFailureException>()
            .Where(exception => exception.Code == PresenterAi.Contracts.ErrorCodes.AuthRequired);
    }

    [Fact]
    public async Task Disabled_user_cannot_rotate_a_refresh_token()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var user = NewUser();
        user.IsDisabled = true;
        const string rawToken = "disabled-user-refresh-token";
        context.Users.Add(user);
        context.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = TokenService.Hash(rawToken),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();

        var service = new TokenService(context, Options.Create(new JwtSettings
        {
            Issuer = "https://integration.presenter-ai.test",
            Audience = "presenter-ai-integration",
            SecretKey = "integration-only-jwt-signing-key-not-a-credential-123456"
        }), TimeProvider.System, NullLogger<TokenService>.Instance);

        var attempt = async () => await service.RotateAsync(rawToken);
        await attempt.Should().ThrowAsync<AuthFailureException>()
            .Where(exception => exception.Code == PresenterAi.Contracts.ErrorCodes.AuthAccountDisabled);
        (await context.RefreshTokens.SingleAsync(token => token.UserId == user.Id)).IsRevoked.Should().BeFalse();
    }

    private async Task<RedemptionResult> RedeemAsync(TokenService service, string token)
    {
        try
        {
            await service.RotateAsync(token);
            return new RedemptionResult(true, null);
        }
        catch (Exception exception)
        {
            return new RedemptionResult(false, exception);
        }
    }

    private PresenterAiDbContext CreateContext() => new(new DbContextOptionsBuilder<PresenterAiDbContext>()
        .UseNpgsql(postgres.ConnectionString)
        .Options);

    private static User NewUser() => new()
    {
        Email = $"refresh-{Guid.NewGuid():N}@example.test",
        DisplayName = "Refresh test user",
        AuthMethod = "test",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private sealed record RedemptionResult(bool Success, Exception? Exception);
}
