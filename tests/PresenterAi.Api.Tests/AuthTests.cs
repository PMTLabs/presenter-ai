using System.Text.Json.Nodes;
using FluentAssertions;
using PresenterAi.Api.Tests.Infrastructure;

namespace PresenterAi.Api.Tests;

public sealed class AuthTests
{
    [Fact]
    public async Task Api_requires_auth_when_dev_scheme_disabled()
    {
        using var factory = new ApiFactory { Overrides = new Dictionary<string, string?> { ["Auth:Dev:Enabled"] = "false" } };
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/config");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
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
    public async Task Jwt_bearer_authenticates_requests_with_a_valid_token()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        (await client.GetAsync("/api/config")).StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }
}
