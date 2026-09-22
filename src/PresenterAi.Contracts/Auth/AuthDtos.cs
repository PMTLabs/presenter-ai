namespace PresenterAi.Contracts.Auth;

public sealed record AuthUserResponse(string Id, string Email, string? DisplayName, string Role);

public sealed record AuthTokenResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    AuthUserResponse User);

public sealed record SsoTokenRequest(string Code, string? CodeVerifier, string? RedirectUri = null);

public sealed record SsoProviderResponse(string Id);

public sealed record ListResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);
