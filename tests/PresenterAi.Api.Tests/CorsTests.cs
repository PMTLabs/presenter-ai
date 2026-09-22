using System.Net;
using FluentAssertions;
using PresenterAi.Api.Tests.Infrastructure;

namespace PresenterAi.Api.Tests;

public sealed class CorsTests
{
    private const string AllowedOrigin = "http://localhost:47914";

    [Theory]
    [InlineData("GET", "/v1/auth/sso/providers")]
    [InlineData("GET", "/v1/config")]
    [InlineData("GET", "/v1/presentations")]
    [InlineData("GET", "/v1/presentations/sample")]
    [InlineData("POST", "/v1/sessions/ticket")]
    public async Task Allowed_origin_gets_non_credentialed_cors_on_every_v1_route_group(string method, string path)
    {
        // Anonymous on purpose: CORS runs before authentication, and an authenticated ticket request would wait on
        // the test host's unreachable database.
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.TryAddWithoutValidation("Origin", AllowedOrigin).Should().BeTrue();

        using var response = await client.SendAsync(request);
        response.Headers.GetValues("Access-Control-Allow-Origin").Single().Should().Be(AllowedOrigin);
        response.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
    }

    [Fact]
    public async Task Disallowed_origin_gets_no_cors_headers()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/presentations");
        request.Headers.TryAddWithoutValidation("Origin", "https://evil.example.test").Should().BeTrue();

        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        response.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
    }

    [Theory]
    [InlineData("/v1/auth/refresh")]
    [InlineData("/v1/auth/logout")]
    public async Task Cookie_mutations_allow_credentials_for_an_allowed_origin(string path)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation("Origin", AllowedOrigin).Should().BeTrue();

        using var response = await client.SendAsync(request);
        response.Headers.GetValues("Access-Control-Allow-Origin").Single().Should().Be(AllowedOrigin);
        response.Headers.GetValues("Access-Control-Allow-Credentials").Single().Should().Be("true");
    }
}
