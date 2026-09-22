using System.Security.Cryptography;
using FluentAssertions;
using PresenterAi.Api.Auth;

namespace PresenterAi.Api.Tests;

public sealed class OAuthSettingsValidatorTests
{
    private static string Key(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));

    private static OAuthSettings Provider(string name, string stateKey) => new()
    {
        ApiBaseUrl = "https://api.example.test",
        StateEncryptionKey = stateKey,
        AllowedRedirectUris = ["https://app.example.test/auth/callback"],
        Google = name == "Google" ? CompleteProvider() : new OAuthProviderSettings(),
        Microsoft = name == "Microsoft" ? CompleteProvider() : new OAuthProviderSettings()
    };

    private static OAuthProviderSettings CompleteProvider() => new()
    {
        Enabled = true,
        ClientId = "client",
        ClientSecret = "secret",
        AuthorizationEndpoint = "https://identity.example.test/authorize",
        TokenEndpoint = "https://identity.example.test/token",
        UserInfoEndpoint = "https://identity.example.test/userinfo"
    };

    [Fact]
    public void No_provider_enabled_does_not_require_a_state_key() =>
        OAuthSettingsValidator.Validate(new OAuthSettings()).Should().BeNull();

    [Fact]
    public void Enabled_provider_requires_a_state_key() =>
        OAuthSettingsValidator.Validate(Provider("Google", string.Empty)).Should().Contain("required");

    [Fact]
    public void Enabled_provider_requires_an_absolute_api_base_url()
    {
        var settings = Provider("Google", Key(32));
        settings.ApiBaseUrl = string.Empty;
        OAuthSettingsValidator.Validate(settings).Should().Contain("ApiBaseUrl");
    }

    [Fact]
    public void State_key_must_be_base64() =>
        OAuthSettingsValidator.Validate(Provider("Google", "not-base64!"))!.Should().Contain("base64");

    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(20)]
    [InlineData(64)]
    public void State_key_rejects_wrong_byte_lengths(int bytes) =>
        OAuthSettingsValidator.Validate(Provider("Google", Key(bytes)))!.Should().Contain($"got {bytes}");

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void State_key_accepts_aes_sizes(int bytes) =>
        OAuthSettingsValidator.Validate(Provider("Google", Key(bytes))).Should().BeNull();

    [Theory]
    [InlineData("Google", "ClientId")]
    [InlineData("Google", "ClientSecret")]
    [InlineData("Microsoft", "ClientId")]
    [InlineData("Microsoft", "ClientSecret")]
    public void Enabled_provider_requires_client_credentials(string providerName, string property)
    {
        var provider = Provider(providerName, Key(32));
        Set(provider, providerName, property, string.Empty);
        OAuthSettingsValidator.Validate(provider).Should().Contain($"{providerName}.{property}");
    }

    [Theory]
    [InlineData("Google", "AuthorizationEndpoint")]
    [InlineData("Google", "TokenEndpoint")]
    [InlineData("Google", "UserInfoEndpoint")]
    [InlineData("Microsoft", "AuthorizationEndpoint")]
    [InlineData("Microsoft", "TokenEndpoint")]
    [InlineData("Microsoft", "UserInfoEndpoint")]
    public void Enabled_provider_requires_secure_endpoints(string providerName, string property)
    {
        var settings = Provider(providerName, Key(32));
        Set(settings, providerName, property, "http://identity.example.test/not-loopback");
        OAuthSettingsValidator.Validate(settings).Should().Contain($"{providerName}.{property}");
    }

    [Theory]
    [InlineData("Google", "AuthorizationEndpoint", "http://localhost.evil.test/authorize")]
    [InlineData("Google", "TokenEndpoint", "http://127.0.0.1.nip.io/token")]
    [InlineData("Google", "UserInfoEndpoint", "http://localhost.evil.test/userinfo")]
    [InlineData("Microsoft", "AuthorizationEndpoint", "http://localhost.evil.test/authorize")]
    [InlineData("Microsoft", "TokenEndpoint", "http://127.0.0.1.nip.io/token")]
    [InlineData("Microsoft", "UserInfoEndpoint", "http://localhost.evil.test/userinfo")]
    public void Enabled_provider_rejects_deceptive_loopback_hosts(string providerName, string property, string endpoint)
    {
        var settings = Provider(providerName, Key(32));
        Set(settings, providerName, property, endpoint);
        OAuthSettingsValidator.Validate(settings).Should().Contain($"{providerName}.{property}");
    }

    [Theory]
    [InlineData("Google")]
    [InlineData("Microsoft")]
    public void Enabled_provider_allows_http_loopback_endpoints(string providerName)
    {
        var settings = Provider(providerName, Key(32));
        Set(settings, providerName, "AuthorizationEndpoint", "http://127.0.0.1/authorize");
        Set(settings, providerName, "TokenEndpoint", "http://localhost/token");
        Set(settings, providerName, "UserInfoEndpoint", "http://[::1]/userinfo");
        OAuthSettingsValidator.Validate(settings).Should().BeNull();
    }

    [Theory]
    [InlineData("Google")]
    [InlineData("Microsoft")]
    public void Enabled_provider_requires_an_absolute_allowed_redirect_uri(string providerName)
    {
        var settings = Provider(providerName, Key(32));
        settings.AllowedRedirectUris = ["not-an-absolute-uri"];
        OAuthSettingsValidator.Validate(settings).Should().Contain("AllowedRedirectUris");
    }

    private static void Set(OAuthSettings settings, string providerName, string property, string value)
    {
        var provider = providerName == "Google" ? settings.Google : settings.Microsoft;
        switch (property)
        {
            case "ClientId": provider.ClientId = value; break;
            case "ClientSecret": provider.ClientSecret = value; break;
            case "AuthorizationEndpoint": provider.AuthorizationEndpoint = value; break;
            case "TokenEndpoint": provider.TokenEndpoint = value; break;
            case "UserInfoEndpoint": provider.UserInfoEndpoint = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(property), property, null);
        }
    }
}
