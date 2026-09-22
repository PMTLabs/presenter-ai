using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Api.Tests.Infrastructure;

namespace PresenterAi.Api.Tests;

public sealed class AuthEndpointSecurityTests
{
    [Theory]
    [InlineData("/v1/auth/refresh")]
    [InlineData("/v1/auth/logout")]
    public async Task Cookie_mutations_require_an_allowed_origin(string path)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var missingOrigin = await client.PostAsync(path, content: null);
        missingOrigin.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await JsonNode.ParseAsync(await missingOrigin.Content.ReadAsStreamAsync()))!["code"]!.GetValue<string>()
            .Should().Be("auth.forbidden");

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(string.Empty)
        };
        request.Headers.TryAddWithoutValidation("Origin", "https://evil.example.test").Should().BeTrue();
        var disallowedOrigin = await client.SendAsync(request);
        disallowedOrigin.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await JsonNode.ParseAsync(await disallowedOrigin.Content.ReadAsStreamAsync()))!["code"]!.GetValue<string>()
            .Should().Be("auth.forbidden");
    }

    [Fact]
    public async Task Sso_providers_use_the_list_envelope()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/v1/auth/sso/providers");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        body["items"].Should().NotBeNull();
        body["page"]!.GetValue<int>().Should().Be(1);
        body["pageSize"]!.GetValue<int>().Should().Be(25);
        body["total"]!.GetValue<int>().Should().Be(0);
        body.Should().NotContainKey("providers");
    }

    [Fact]
    public async Task Rate_limited_routes_emit_all_rate_limit_headers_and_retry_after()
    {
        using var factory = new ApiFactory
        {
            Overrides = new Dictionary<string, string?>
            {
                ["RateLimiting:Enabled"] = "true"
            }
        };
        using var client = factory.CreateClient();
        var url = "/v1/auth/sso/google/authorize?redirect_uri=x&code_challenge=short&state=s";
        HttpResponseMessage? last = null;
        for (var index = 0; index < 21; index++)
        {
            last?.Dispose();
            last = await client.GetAsync(url);
        }

        last!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        last.Headers.Contains("RateLimit-Limit").Should().BeTrue();
        last.Headers.Contains("RateLimit-Remaining").Should().BeTrue();
        last.Headers.Contains("RateLimit-Reset").Should().BeTrue();
        last.Headers.RetryAfter.Should().NotBeNull();
        last.Dispose();
    }

    [Fact]
    public void Dev_sign_in_is_absent_outside_development_and_mapped_in_development()
    {
        using var testing = new ApiFactory();
        testing.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Should().NotContain(endpoint => endpoint.RoutePattern.RawText == "/v1/auth/dev/sign-in");

        using var development = new ApiFactory { EnvironmentName = "Development" };
        development.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Should().Contain(endpoint => endpoint.RoutePattern.RawText == "/v1/auth/dev/sign-in");
    }
}
