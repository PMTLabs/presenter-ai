using System.Net;
using FluentAssertions;
using PresenterAi.Api.Tests.Infrastructure;

namespace PresenterAi.Api.Tests;

public sealed class SecurityHeaderTests
{
    [Fact]
    public async Task V1_responses_are_no_store_and_carry_a_request_id()
    {
        const string requestId = "client-request:42";
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        await AssertV1Headers(client, new HttpRequestMessage(HttpMethod.Get, "/v1/auth/sso/providers"), requestId, HttpStatusCode.OK);
        await AssertV1Headers(client, new HttpRequestMessage(HttpMethod.Get, "/v1/config"), requestId, HttpStatusCode.Unauthorized);
        await AssertV1Headers(client, new HttpRequestMessage(HttpMethod.Get, "/v1/auth/sso/google/authorize"), requestId, HttpStatusCode.BadRequest);
        await AssertV1Headers(client, new HttpRequestMessage(HttpMethod.Get, "/v1/does-not-exist"), requestId, HttpStatusCode.NotFound);

        const string malformed = "not/a-valid-request-id";
        using var malformedRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/auth/sso/providers");
        malformedRequest.Headers.TryAddWithoutValidation("X-Request-Id", malformed).Should().BeTrue();
        using var malformedResponse = await client.SendAsync(malformedRequest);
        malformedResponse.Headers.GetValues("X-Request-Id").Single().Should().NotBe(malformed);
        malformedResponse.Headers.GetValues("X-Request-Id").Single().Should().MatchRegex("^[A-Za-z0-9._:-]{1,128}$");
        malformedResponse.Headers.CacheControl!.ToString().Should().Be("no-store");

        using var health = await client.GetAsync("/health");
        health.Headers.CacheControl?.ToString().Should().NotBe("no-store");

        using var rateLimitedFactory = new ApiFactory
        {
            Overrides = new Dictionary<string, string?> { ["RateLimiting:Enabled"] = "true" }
        };
        using var rateLimitedClient = rateLimitedFactory.CreateClient();
        HttpResponseMessage? rejected = null;
        for (var attempt = 0; attempt < 21; attempt++)
        {
            rejected?.Dispose();
            rejected = await Request(rateLimitedClient, HttpMethod.Get,
                "/v1/auth/sso/google/authorize?redirect_uri=https%3A%2F%2Fapp.example.test&code_challenge=challenge&state=state", requestId);
        }

        rejected!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.CacheControl!.ToString().Should().Be("no-store");
        rejected.Headers.GetValues("X-Request-Id").Single().Should().Be(requestId);
        rejected.Dispose();
    }

    private static async Task AssertV1Headers(HttpClient client, HttpRequestMessage request, string requestId, HttpStatusCode status)
    {
        using (request)
        using (var response = await Send(client, request, requestId))
        {
            response.StatusCode.Should().Be(status);
            response.Headers.CacheControl!.ToString().Should().Be("no-store");
            response.Headers.GetValues("X-Request-Id").Single().Should().Be(requestId);
        }
    }

    private static Task<HttpResponseMessage> Request(HttpClient client, HttpMethod method, string path, string requestId) =>
        Send(client, new HttpRequestMessage(method, path), requestId);

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpRequestMessage request, string requestId)
    {
        request.Headers.TryAddWithoutValidation("X-Request-Id", requestId).Should().BeTrue();
        return client.SendAsync(request);
    }
}
