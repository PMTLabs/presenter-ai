// Origin: InkSpoke API, commit b83e691f; copied for presenter-ai plan-004.
namespace PresenterAi.Api.Auth;

public sealed class OAuthSettings
{
    public const string SectionName = "OAuth";

    public OAuthProviderSettings Google { get; set; } = new();
    public OAuthProviderSettings Microsoft { get; set; } = new();
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string StateEncryptionKey { get; set; } = string.Empty;
    public string[] AllowedRedirectUris { get; set; } = [];

    public bool AnyProviderEnabled => Google.Enabled || Microsoft.Enabled;
}

public static class OAuthSettingsValidator
{
    public static string? Validate(OAuthSettings settings)
    {
        if (!settings.AnyProviderEnabled)
            return null;

        if (string.IsNullOrWhiteSpace(settings.StateEncryptionKey))
            return "OAuth.StateEncryptionKey is required when any provider is enabled.";

        if (!Uri.TryCreate(settings.ApiBaseUrl, UriKind.Absolute, out var apiBaseUri)
            || apiBaseUri.Scheme is not ("http" or "https"))
        {
            return "OAuth.ApiBaseUrl must be an absolute HTTP or HTTPS URL when any provider is enabled.";
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(settings.StateEncryptionKey);
        }
        catch (FormatException)
        {
            return "OAuth.StateEncryptionKey must be valid base64.";
        }

        return decoded.Length is 16 or 24 or 32
            ? null
            : $"OAuth.StateEncryptionKey must decode to 16, 24, or 32 bytes for AES; got {decoded.Length}.";
    }
}

public sealed class OAuthProviderSettings
{
    public bool Enabled { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string AuthorizationEndpoint { get; set; } = string.Empty;
    public string TokenEndpoint { get; set; } = string.Empty;
    public string UserInfoEndpoint { get; set; } = string.Empty;
    public string? TenantId { get; set; }
}
