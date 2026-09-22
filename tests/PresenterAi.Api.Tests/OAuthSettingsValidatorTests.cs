using System.Security.Cryptography;
using FluentAssertions;
using PresenterAi.Api.Auth;

namespace PresenterAi.Api.Tests;

public sealed class OAuthSettingsValidatorTests
{
    private static string Key(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));

    private static OAuthSettings Google(string stateKey) => new()
    {
        ApiBaseUrl = "https://api.example.test",
        StateEncryptionKey = stateKey,
        Google = new OAuthProviderSettings { Enabled = true, ClientId = "client" }
    };

    [Fact]
    public void No_provider_enabled_does_not_require_a_state_key() =>
        OAuthSettingsValidator.Validate(new OAuthSettings()).Should().BeNull();

    [Fact]
    public void Enabled_provider_requires_a_state_key() =>
        OAuthSettingsValidator.Validate(Google(string.Empty)).Should().Contain("required");

    [Fact]
    public void Enabled_provider_requires_an_absolute_api_base_url()
    {
        var settings = Google(Key(32));
        settings.ApiBaseUrl = string.Empty;
        OAuthSettingsValidator.Validate(settings).Should().Contain("ApiBaseUrl");
    }

    [Fact]
    public void State_key_must_be_base64() =>
        OAuthSettingsValidator.Validate(Google("not-base64!"))!.Should().Contain("base64");

    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(20)]
    [InlineData(64)]
    public void State_key_rejects_wrong_byte_lengths(int bytes) =>
        OAuthSettingsValidator.Validate(Google(Key(bytes)))!.Should().Contain($"got {bytes}");

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void State_key_accepts_aes_sizes(int bytes) =>
        OAuthSettingsValidator.Validate(Google(Key(bytes))).Should().BeNull();
}
