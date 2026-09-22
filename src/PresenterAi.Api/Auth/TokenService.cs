using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PresenterAi.Api.Errors;
using PresenterAi.Contracts;
using PresenterAi.Contracts.Auth;
using PresenterAi.Infrastructure.Persistence;
using PresenterAi.Infrastructure.Persistence.Entities;

namespace PresenterAi.Api.Auth;

public sealed class AuthFailureException(string code, int status, string detail) : Exception(detail)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public string Detail { get; } = detail;
}

public sealed record IssuedTokens(string AccessToken, DateTimeOffset ExpiresAt, string RefreshToken, User User);

public sealed class TokenService(
    PresenterAiDbContext db,
    IOptions<JwtSettings> settings,
    TimeProvider timeProvider,
    ILogger<TokenService> logger)
{
    private readonly JwtSettings _settings = settings.Value;

    public async Task<IssuedTokens> IssueAsync(User user, CancellationToken cancellationToken = default)
    {
        EnsureEnabled(user);
        var now = timeProvider.GetUtcNow();
        var expiresAt = now.AddMinutes(_settings.AccessTokenMinutes);
        var accessToken = CreateAccessToken(user, expiresAt);
        var refreshToken = CreateOpaqueToken();

        if (db.Entry(user).State == EntityState.Detached)
            db.Users.Attach(user);
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = Hash(refreshToken),
            ExpiresAt = now.AddDays(_settings.RefreshTokenDays),
            CreatedAt = now,
            IsRevoked = false
        });
        user.LastSignInAt = now;
        user.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new IssuedTokens(accessToken, expiresAt, refreshToken, user);
    }

    public async Task<User> UpsertDevelopmentUserAsync(IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var configuredId = configuration["Auth:Dev:UserId"];
        var email = configuration["Auth:Dev:Email"]?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email))
            throw new AuthFailureException(ErrorCodes.AuthSignupNotAllowed, StatusCodes.Status403Forbidden, "A development email is required.");

        var user = !string.IsNullOrWhiteSpace(configuredId)
            ? await db.Users.SingleOrDefaultAsync(candidate => candidate.Id == configuredId, cancellationToken).ConfigureAwait(false)
            : null;
        user ??= await db.Users.SingleOrDefaultAsync(candidate => candidate.Email.ToLower() == email, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        if (user is null)
        {
            user = new User
            {
                Email = email,
                DisplayName = configuration["Auth:Dev:DisplayName"],
                Role = configuration.GetSection("Admin:BootstrapEmails").Get<string[]>()?.Any(value =>
                    string.Equals(value.Trim(), email, StringComparison.OrdinalIgnoreCase)) == true ? "admin" : "user",
                AuthMethod = "dev",
                CreatedAt = now,
                UpdatedAt = now
            };
            if (!string.IsNullOrWhiteSpace(configuredId))
                user.Id = configuredId;
            db.Users.Add(user);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (user.IsDisabled)
        {
            throw new AuthFailureException(ErrorCodes.AuthAccountDisabled, StatusCodes.Status403Forbidden, "This account is disabled.");
        }
        return user;
    }

    public async Task<IssuedTokens> RotateAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var hash = Hash(refreshToken);
        var stored = await db.RefreshTokens
            .AsNoTracking()
            .Include(token => token.User)
            .SingleOrDefaultAsync(token => token.TokenHash == hash, cancellationToken)
            .ConfigureAwait(false);

        if (stored is null || stored.ExpiresAt <= timeProvider.GetUtcNow())
        {
            if (stored is not null)
                await RevokeAsync(stored.Id, cancellationToken).ConfigureAwait(false);
            throw Required();
        }

        EnsureEnabled(stored.User);

        // The predicate is deliberately repeated in the UPDATE. A transaction around the read
        // would allow two concurrent redeemers to both mint a successor.
        var affected = await db.RefreshTokens
            .Where(token => token.Id == stored.Id && !token.IsRevoked)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(token => token.IsRevoked, true)
                .SetProperty(token => token.RevokedAt, timeProvider.GetUtcNow()), cancellationToken)
            .ConfigureAwait(false);
        if (affected != 1)
            throw Required();

        return await IssueAsync(stored.User, cancellationToken).ConfigureAwait(false);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var hash = Hash(refreshToken);
        await db.RefreshTokens
            .Where(token => token.TokenHash == hash && !token.IsRevoked)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(token => token.IsRevoked, true)
                .SetProperty(token => token.RevokedAt, timeProvider.GetUtcNow()), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<User> GetUserAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var id = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(id))
            throw Required();

        var user = await db.Users.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
            throw Required();
        EnsureEnabled(user);
        return user;
    }

    public static AuthTokenResponse ToResponse(IssuedTokens tokens) =>
        new(tokens.AccessToken, tokens.ExpiresAt, ToUserResponse(tokens.User));

    public static AuthUserResponse ToUserResponse(User user) =>
        new(user.Id, user.Email, user.DisplayName, user.Role);

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private string CreateAccessToken(User user, DateTimeOffset expiresAt)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.SecretKey));
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("role", user.Role),
            new(ClaimTypes.Role, user.Role),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        if (user.DisplayName is not null)
            claims.Add(new Claim("display_name", user.DisplayName));

        var token = new JwtSecurityToken(
            issuer: _settings.Issuer,
            audience: _settings.Audience,
            claims: claims,
            notBefore: timeProvider.GetUtcNow().UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task RevokeAsync(string id, CancellationToken cancellationToken) =>
        await db.RefreshTokens
            .Where(token => token.Id == id && !token.IsRevoked)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(token => token.IsRevoked, true)
                .SetProperty(token => token.RevokedAt, timeProvider.GetUtcNow()), cancellationToken)
            .ConfigureAwait(false);

    private void EnsureEnabled(User user)
    {
        if (!user.IsDisabled)
            return;

        logger.LogWarning("Disabled user {UserId} attempted token issuance", user.Id);
        throw new AuthFailureException(
            ErrorCodes.AuthAccountDisabled,
            StatusCodes.Status403Forbidden,
            "This account is disabled.");
    }

    private static AuthFailureException Required() => new(
        ErrorCodes.AuthRequired,
        StatusCodes.Status401Unauthorized,
        "Authentication is required.");

    private static string CreateOpaqueToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))
        .Replace("+", "-", StringComparison.Ordinal)
        .Replace("/", "_", StringComparison.Ordinal)
        .TrimEnd('=');
}
