using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Api.Tests.Infrastructure;
using PresenterAi.Application.Content;

namespace PresenterAi.Api.Tests;

public sealed class AuthTests
{
    [Fact]
    public async Task Api_requires_auth_when_dev_scheme_disabled()
    {
        using var factory = new ApiFactory { Overrides = new Dictionary<string, string?> { ["Auth:Dev:Enabled"] = "false" } };
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/v1/config");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        body["code"]!.GetValue<string>().Should().Be("auth.required");
        body["type"]!.GetValue<string>().Should().Be("https://presenter-ai.dev/errors/auth.required");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        response.Headers.WwwAuthenticate.ToString().Should().Be("Bearer");
        // The challenge is a Problem Details producer like any other: header and body carry the same W3C id.
        response.Headers.TryGetValues("traceparent", out var traceparent).Should().BeTrue();
        body["traceId"]!.GetValue<string>().Should().Be(traceparent!.Single());
    }

    [Fact]
    public void Api_host_does_not_register_the_ownerless_import_source()
    {
        using var factory = new ApiFactory();
        factory.Services.GetService<IPresentationImportSource>().Should().BeNull();
    }

    [Fact]
    public async Task Jwt_bearer_authenticates_requests_with_a_valid_token()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        (await client.GetAsync("/v1/config")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Jwt_rejects_a_token_with_the_wrong_issuer()
    {
        using var factory = new ApiFactory();
        using var client = ClientWith(factory, ApiFactory.CreateTestToken("test-user", "test@presenter-ai.local", issuer: "https://other.example.test"));
        (await client.GetAsync("/v1/config")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Jwt_rejects_a_token_with_the_wrong_audience()
    {
        using var factory = new ApiFactory();
        using var client = ClientWith(factory, ApiFactory.CreateTestToken("test-user", "test@presenter-ai.local", audience: "another-audience"));
        (await client.GetAsync("/v1/config")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Jwt_rejects_a_token_with_the_wrong_signing_key()
    {
        using var factory = new ApiFactory();
        using var client = ClientWith(factory, ApiFactory.CreateTestToken("test-user", "test@presenter-ai.local", signingKey: "another-test-only-jwt-secret-key-not-a-credential-123456"));
        (await client.GetAsync("/v1/config")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Jwt_rejects_a_token_expired_beyond_the_clock_skew()
    {
        var now = DateTime.UtcNow;
        using var factory = new ApiFactory();
        using var client = ClientWith(factory, ApiFactory.CreateTestToken(
            "test-user", "test@presenter-ai.local", notBefore: now.AddMinutes(-10), expires: now.AddSeconds(-31)));
        (await client.GetAsync("/v1/config")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Jwt_rejects_a_token_not_yet_valid_beyond_the_clock_skew()
    {
        var now = DateTime.UtcNow;
        using var factory = new ApiFactory();
        using var client = ClientWith(factory, ApiFactory.CreateTestToken(
            "test-user", "test@presenter-ai.local", notBefore: now.AddSeconds(31), expires: now.AddMinutes(10)));
        (await client.GetAsync("/v1/config")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Jwt_accepts_a_token_expired_within_the_clock_skew()
    {
        var now = DateTime.UtcNow;
        using var factory = new ApiFactory();
        using var client = ClientWith(factory, ApiFactory.CreateTestToken(
            "test-user", "test@presenter-ai.local", notBefore: now.AddMinutes(-10), expires: now.AddSeconds(-20)));
        (await client.GetAsync("/v1/config")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static HttpClient ClientWith(ApiFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
