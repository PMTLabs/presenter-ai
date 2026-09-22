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

        if (decoded.Length is not (16 or 24 or 32))
            return $"OAuth.StateEncryptionKey must decode to 16, 24, or 32 bytes for AES; got {decoded.Length}.";

        if (!settings.AllowedRedirectUris.Any(IsAbsoluteUri))
            return "OAuth.AllowedRedirectUris must contain at least one absolute URI when any provider is enabled.";

        foreach (var (name, provider) in Providers(settings))
        {
            if (!provider.Enabled)
                continue;

            if (string.IsNullOrWhiteSpace(provider.ClientId))
                return $"OAuth.{name}.ClientId is required when the provider is enabled.";
            if (string.IsNullOrWhiteSpace(provider.ClientSecret))
                return $"OAuth.{name}.ClientSecret is required when the provider is enabled.";

            foreach (var (endpointName, endpoint) in Endpoints(provider))
            {
                if (!IsSecureEndpoint(endpoint))
                    return $"OAuth.{name}.{endpointName} must be an absolute HTTPS URI (or HTTP loopback URI) when the provider is enabled.";
            }
        }

        return null;
    }

    private static IEnumerable<(string Name, OAuthProviderSettings Provider)> Providers(OAuthSettings settings)
    {
        yield return ("Google", settings.Google);
        yield return ("Microsoft", settings.Microsoft);
    }

    private static IEnumerable<(string Name, string Value)> Endpoints(OAuthProviderSettings provider)
    {
        yield return ("AuthorizationEndpoint", provider.AuthorizationEndpoint);
        yield return ("TokenEndpoint", provider.TokenEndpoint);
        yield return ("UserInfoEndpoint", provider.UserInfoEndpoint);
    }

    private static bool IsAbsoluteUri(string value) => Uri.TryCreate(value, UriKind.Absolute, out _);

    private static bool IsSecureEndpoint(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
}

public sealed class OAuthProviderSettings
{
    public bool Enabled { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string AuthorizationEndpoint { get; set; } = string.Empty;
    public string TokenEndpoint { get; set; } = string.Empty;
    public string UserInfoEndpoint { get; set; } = string.Empty;
}
