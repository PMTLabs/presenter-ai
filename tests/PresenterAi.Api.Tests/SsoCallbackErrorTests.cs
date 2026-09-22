using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using PresenterAi.Api.Tests.Infrastructure;

namespace PresenterAi.Api.Tests;

public sealed class SsoCallbackErrorTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    private static readonly string KeyBase64 = Convert.ToBase64String(Key);

    [Fact]
    public async Task Undecryptable_state_returns_the_fixed_callback_message()
    {
        using var client = CreateClient();
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var response = await client.GetAsync($"/v1/auth/sso/google/callback?code=abc&state={Uri.EscapeDataString(state)}");
        await AssertFixedMessage(response);
    }

    [Fact]
    public async Task Provider_bound_state_does_not_reveal_the_other_provider()
    {
        using var client = CreateClient();
        var state = Seal("microsoft", DateTimeOffset.UtcNow);
        var response = await client.GetAsync($"/v1/auth/sso/google/callback?code=abc&state={Uri.EscapeDataString(state)}");
        await AssertFixedMessage(response, forbiddenWords: ["microsoft", "mismatch"]);
    }

    [Fact]
    public async Task Expired_state_does_not_reveal_the_lifetime_window()
    {
        using var client = CreateClient();
        var state = Seal("google", DateTimeOffset.UtcNow.AddDays(-1));
        var response = await client.GetAsync($"/v1/auth/sso/google/callback?code=abc&state={Uri.EscapeDataString(state)}");
        await AssertFixedMessage(response, forbiddenWords: ["expired", "minutes old"]);
    }

    private static HttpClient CreateClient()
    {
        var factory = new ApiFactory
        {
            Overrides = new Dictionary<string, string?>
            {
                ["OAuth:ApiBaseUrl"] = "https://api.example.test",
                ["OAuth:StateEncryptionKey"] = KeyBase64,
                ["OAuth:AllowedRedirectUris:0"] = "https://app.example.test/callback",
                ["OAuth:Google:Enabled"] = "true",
                ["OAuth:Google:ClientId"] = "google-client",
                ["OAuth:Google:ClientSecret"] = "not-a-secret",
                ["OAuth:Google:AuthorizationEndpoint"] = "https://provider.test/authorize",
                ["OAuth:Google:TokenEndpoint"] = "https://provider.test/token",
                ["OAuth:Google:UserInfoEndpoint"] = "https://provider.test/userinfo"
            }
        };
        return factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    private static async Task AssertFixedMessage(HttpResponseMessage response, params string[] forbiddenWords)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        body["detail"]!.GetValue<string>().Should().Be("Sign-in could not be completed. Please try again.");
        foreach (var word in forbiddenWords)
            body.ToJsonString().ToLowerInvariant().Should().NotContain(word.ToLowerInvariant());
    }

    private static string Seal(string provider, DateTimeOffset timestamp)
    {
        var json = JsonSerializer.Serialize(new
        {
            state = "client-state",
            clientHint = "web",
            redirectUri = "https://app.example.test/callback",
            provider,
            codeChallenge = "A".PadRight(43, 'A'),
            nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            timestamp
        });
        var clear = Encoding.UTF8.GetBytes(json);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[clear.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Key, 16);
        aes.Encrypt(nonce, clear, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }
}
