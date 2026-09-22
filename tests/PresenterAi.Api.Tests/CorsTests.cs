using System.Net;
using FluentAssertions;
using PresenterAi.Api.Tests.Infrastructure;

namespace PresenterAi.Api.Tests;

public sealed class CorsTests
{
    private const string AllowedOrigin = "http://localhost:47914";

    [Fact]
    public async Task Allowed_origin_gets_non_credentialed_cors_on_bearer_routes()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/v1/presentations");
        preflight.Headers.TryAddWithoutValidation("Origin", AllowedOrigin).Should().BeTrue();
        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET").Should().BeTrue();
        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "authorization,content-type,x-request-id").Should().BeTrue();
        using var preflightResponse = await client.SendAsync(preflight);
        preflightResponse.IsSuccessStatusCode.Should().BeTrue();
        preflightResponse.Headers.GetValues("Access-Control-Allow-Origin").Single().Should().Be(AllowedOrigin);
        preflightResponse.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/presentations");
        request.Headers.TryAddWithoutValidation("Origin", AllowedOrigin).Should().BeTrue();
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
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

    [Fact]
    public async Task Refresh_allows_credentials_for_an_allowed_origin()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/refresh");
        request.Headers.TryAddWithoutValidation("Origin", AllowedOrigin).Should().BeTrue();

        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.GetValues("Access-Control-Allow-Origin").Single().Should().Be(AllowedOrigin);
        response.Headers.GetValues("Access-Control-Allow-Credentials").Single().Should().Be("true");
    }
}
