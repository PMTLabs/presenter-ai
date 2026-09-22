// Origin: InkSpoke API, commit b83e691f; copied for presenter-ai plan-004.
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PresenterAi.Application.Auth;
using PresenterAi.Infrastructure.Persistence;
using PresenterAi.Infrastructure.Persistence.Entities;

namespace PresenterAi.Infrastructure.Identity;

public sealed class OAuthOptions
{
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string StateEncryptionKey { get; set; } = string.Empty;
    public string[] AllowedRedirectUris { get; set; } = [];
    public OAuthProviderOptions Google { get; set; } = new();
    public OAuthProviderOptions Microsoft { get; set; } = new();
}

public sealed class OAuthProviderOptions
{
    public bool Enabled { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string AuthorizationEndpoint { get; set; } = string.Empty;
    public string TokenEndpoint { get; set; } = string.Empty;
    public string UserInfoEndpoint { get; set; } = string.Empty;
    public string? TenantId { get; set; }
}

public sealed class SsoFailureException(string code, int status, string detail) : Exception(detail)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public string Detail => Message;
}

public sealed class SsoService(
    PresenterAiDbContext db,
    IOptions<OAuthOptions> options,
    IHttpClientFactory httpClientFactory,
    Lazy<ISsoCodeStore> codeStore,
    Lazy<ISsoStateStore> stateStore,
    SignInPolicy signInPolicy,
    TimeProvider timeProvider,
    ILogger<SsoService> logger)
{
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    private readonly OAuthOptions _options = options.Value;

    public IReadOnlyList<string> GetEnabledProviders()
    {
        var providers = new List<string>(2);
        if (_options.Google.Enabled) providers.Add("google");
        if (_options.Microsoft.Enabled) providers.Add("microsoft");
        return providers;
    }

    public string BuildAuthorizationUrl(string provider, string redirectUri, string codeChallenge, string state, string? clientHint)
    {
        var settings = GetProvider(provider);
        if (settings is null || !settings.Enabled)
            throw Failure("auth.sso_provider_disabled", 400, "The requested SSO provider is disabled.");
        if (!_options.AllowedRedirectUris.Contains(redirectUri, StringComparer.Ordinal))
            throw Failure("auth.sso_state_invalid", 400, "The redirect URI is not allowed.");
        if (!IsWellFormedS256Challenge(codeChallenge))
            throw Failure("auth.sso_state_invalid", 400, "A valid S256 code challenge is required.");

        var payload = JsonSerializer.Serialize(new
        {
            state,
            clientHint = clientHint ?? "web",
            redirectUri,
            provider,
            codeChallenge,
            nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            timestamp = timeProvider.GetUtcNow()
        });
        var sealedState = Encrypt(payload);
        var callback = $"{_options.ApiBaseUrl.TrimEnd('/')}/v1/auth/sso/{provider}/callback";
        return $"{settings.AuthorizationEndpoint}?client_id={Uri.EscapeDataString(settings.ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(callback)}&response_type=code" +
            $"&scope={Uri.EscapeDataString("openid email profile")}" +
            $"&state={Uri.EscapeDataString(sealedState)}";
    }

    public async Task<string> HandleCallbackAsync(
        string provider,
        string authorizationCode,
        string sealedState,
        CancellationToken cancellationToken = default)
    {
        var payload = ReadState(sealedState);
        var stateProvider = RequiredString(payload, "provider");
        if (!string.Equals(stateProvider, provider, StringComparison.Ordinal))
            throw new InvalidOperationException("SSO state provider mismatch.");
        if (!payload.TryGetProperty("timestamp", out var timestampProperty)
            || !timestampProperty.TryGetDateTimeOffset(out var issuedAt)
            || timeProvider.GetUtcNow() - issuedAt > StateLifetime
            || timeProvider.GetUtcNow() - issuedAt < TimeSpan.FromMinutes(-1))
        {
            throw new InvalidOperationException("SSO state is expired or invalid.");
        }

        var nonce = RequiredString(payload, "nonce");
        if (!await stateStore.Value.TryConsumeNonceAsync(nonce, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("SSO state has already been used.");

        var identity = await ExchangeCodeAsync(provider, authorizationCode, cancellationToken).ConfigureAwait(false);
        var user = await ResolveIdentityAsync(identity, cancellationToken).ConfigureAwait(false);
        if (user.IsDisabled)
            throw Failure("auth.account_disabled", 403, "This account is disabled.");

        var code = CreateOpaqueToken();
        await codeStore.Value.IssueAsync(
            Hash(code),
            new SsoCode(user.Id, RequiredString(payload, "codeChallenge"), RequiredString(payload, "redirectUri")),
            cancellationToken).ConfigureAwait(false);

        return $"{RequiredString(payload, "redirectUri")}?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(RequiredString(payload, "state"))}";
    }

    public async Task<User> RedeemCodeAsync(string code, string? codeVerifier, CancellationToken cancellationToken = default)
    {
        var stored = await codeStore.Value.ClaimAsync(Hash(code), cancellationToken).ConfigureAwait(false);
        if (stored is null)
            throw Failure("auth.sso_code_used", 400, "The SSO code has expired or was already used.");
        if (!VerifyPkce(stored.CodeChallenge, codeVerifier))
            throw Failure("auth.sso_state_invalid", 400, "PKCE verification failed.");

        var user = await db.Users.SingleOrDefaultAsync(candidate => candidate.Id == stored.UserId, cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
            throw Failure("auth.sso_code_used", 400, "The SSO code has expired or was already used.");
        if (user.IsDisabled)
            throw Failure("auth.account_disabled", 403, "This account is disabled.");
        return user;
    }

    public async Task<User> ResolveIdentityAsync(ProviderIdentity identity, CancellationToken cancellationToken = default)
    {
        var existingLogin = await db.ExternalLogins
            .Include(login => login.User)
            .SingleOrDefaultAsync(login => login.Provider == identity.Provider && login.Subject == identity.Subject, cancellationToken)
            .ConfigureAwait(false);
        if (existingLogin is not null)
            return existingLogin.User;

        var normalizedEmail = identity.Email?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(normalizedEmail))
        {
            var existingUser = await db.Users
                .SingleOrDefaultAsync(user => user.Email.ToLower() == normalizedEmail, cancellationToken)
                .ConfigureAwait(false);
            if (existingUser is not null)
            {
                if (!identity.EmailVerified)
                {
                    logger.LogWarning("Refused unverified SSO email collision for provider {Provider}", identity.Provider);
                    throw Failure("auth.signup_not_allowed", 403, "This sign-in cannot be linked to an existing account.");
                }

                AddExternalLogin(existingUser, identity, normalizedEmail);
                existingUser.UpdatedAt = timeProvider.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return existingUser;
            }
        }

        if (!signInPolicy.IsAllowed(normalizedEmail))
            throw Failure("auth.signup_not_allowed", 403, "This sign-in is not allowed.");

        var now = timeProvider.GetUtcNow();
        var user = new User
        {
            Email = normalizedEmail ?? $"sso-{identity.Provider}-{identity.Subject}@invalid.local",
            DisplayName = identity.DisplayName,
            Role = normalizedEmail is not null && signInPolicy.IsBootstrapAdmin(normalizedEmail) ? "admin" : "user",
            AuthMethod = identity.Provider,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Users.Add(user);
        AddExternalLogin(user, identity, normalizedEmail ?? string.Empty);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return user;
    }

    private async Task<ProviderIdentity> ExchangeCodeAsync(string provider, string code, CancellationToken cancellationToken)
    {
        var settings = GetProvider(provider) ?? throw Failure("auth.sso_provider_disabled", 400, "The requested SSO provider is disabled.");
        var client = httpClientFactory.CreateClient("sso");
        var callback = $"{_options.ApiBaseUrl.TrimEnd('/')}/v1/auth/sso/{provider}/callback";
        using var tokenResponse = await client.PostAsync(settings.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = settings.ClientId,
            ["client_secret"] = settings.ClientSecret,
            ["redirect_uri"] = callback,
            ["grant_type"] = "authorization_code",
            ["scope"] = "openid email profile"
        }), cancellationToken).ConfigureAwait(false);
        var tokenBody = await tokenResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!tokenResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"{provider} token exchange returned HTTP {(int)tokenResponse.StatusCode}.");

        using var tokenJson = JsonDocument.Parse(tokenBody);
        if (!tokenJson.RootElement.TryGetProperty("access_token", out var accessTokenProperty)
            || string.IsNullOrWhiteSpace(accessTokenProperty.GetString()))
            throw new InvalidOperationException($"{provider} token response did not contain an access token.");

        using var request = new HttpRequestMessage(HttpMethod.Get, settings.UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessTokenProperty.GetString());
        using var userInfoResponse = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var userInfoBody = await userInfoResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!userInfoResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"{provider} user info returned HTTP {(int)userInfoResponse.StatusCode}.");
        using var userInfo = JsonDocument.Parse(userInfoBody);
        return ParseIdentity(provider, userInfo.RootElement);
    }

    private static ProviderIdentity ParseIdentity(string provider, JsonElement json)
    {
        var subject = provider == "microsoft"
            ? GetString(json, "id")
            : GetString(json, "sub");
        if (string.IsNullOrWhiteSpace(subject))
            throw new InvalidOperationException("SSO user info did not contain a subject.");

        var email = GetString(json, "email")
            ?? (provider == "microsoft" ? GetString(json, "mail") ?? GetString(json, "userPrincipalName") : null);
        var displayName = GetString(json, "name") ?? GetString(json, "displayName");
        var verified = GetBool(json, "email_verified") ?? GetBool(json, "verified") ?? false;
        return new ProviderIdentity(provider, subject, email, displayName, verified);
    }

    private void AddExternalLogin(User user, ProviderIdentity identity, string email) =>
        user.ExternalLogins.Add(new ExternalLogin
        {
            UserId = user.Id,
            Provider = identity.Provider,
            Subject = identity.Subject,
            ProviderEmail = email,
            ProviderEmailVerified = identity.EmailVerified,
            ProviderDisplayName = identity.DisplayName,
            CreatedAt = timeProvider.GetUtcNow()
        });

    private JsonElement ReadState(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length < 29) throw new FormatException();
            var plaintext = new byte[bytes.Length - 28];
            using var aes = new AesGcm(Convert.FromBase64String(_options.StateEncryptionKey), 16);
            aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plaintext);
            return JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(plaintext));
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or JsonException or ArgumentException)
        {
            throw new InvalidOperationException("SSO state could not be opened.", exception);
        }
    }

    private string Encrypt(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var clear = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[clear.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Convert.FromBase64String(_options.StateEncryptionKey), 16);
        aes.Encrypt(nonce, clear, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    private OAuthProviderOptions? GetProvider(string provider) => provider switch
    {
        "google" => _options.Google,
        "microsoft" => _options.Microsoft,
        _ => null
    };

    private static string RequiredString(JsonElement payload, string property) =>
        payload.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidOperationException($"SSO state is missing {property}.");

    private static string? GetString(JsonElement json, string property) =>
        json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? GetBool(JsonElement json, string property) =>
        json.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static bool VerifyPkce(string challenge, string? verifier)
    {
        if (verifier is not { Length: >= 43 and <= 128 }) return false;
        var computed = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)))
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(computed), Encoding.UTF8.GetBytes(challenge));
    }

    private static bool IsWellFormedS256Challenge(string challenge) =>
        challenge is { Length: >= 43 and <= 128 }
        && challenge.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    private static SsoFailureException Failure(string code, int status, string detail) => new(code, status, detail);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string CreateOpaqueToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace("+", "-", StringComparison.Ordinal).Replace("/", "_", StringComparison.Ordinal).TrimEnd('=');

    public sealed record ProviderIdentity(string Provider, string Subject, string? Email, string? DisplayName, bool EmailVerified);
}
