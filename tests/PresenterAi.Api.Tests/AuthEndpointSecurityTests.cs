using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
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

    [Theory]
    [InlineData("/v1/auth/sso/google/authorize?redirect_uri=https%3A%2F%2Fapp.example.test&code_challenge=challenge&state=state", 20)]
    [InlineData("/v1/auth/sso/google/callback?code=code&state=state", 20)]
    [InlineData("/v1/auth/sso/token", 30)]
    [InlineData("/v1/auth/refresh", 30)]
    public async Task Rate_limited_routes_advertise_the_limit_at_which_the_next_request_is_rejected(string path, int limit)
    {
        using var factory = new ApiFactory
        {
            Overrides = new Dictionary<string, string?> { ["RateLimiting:Enabled"] = "true" }
        };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        for (var request = 1; request <= limit; request++)
        {
            using var response = await client.SendAsync(RateLimitRequest(path));
            response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        }

        using var rejected = await client.SendAsync(RateLimitRequest(path));
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        Header(rejected, "RateLimit-Limit").Should().Be(limit.ToString());
        Header(rejected, "RateLimit-Remaining").Should().Be("0");
        var reset = Header(rejected, "RateLimit-Reset");
        var retryAfter = rejected.Headers.RetryAfter!.Delta!.Value.TotalSeconds;
        retryAfter.Should().BeInRange(1, 60);
        retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture).Should().Be(reset);
    }

    [Fact]
    public async Task Rate_limit_headers_follow_endpoint_metadata_for_case_insensitive_routes()
    {
        using var factory = new ApiFactory
        {
            Overrides = new Dictionary<string, string?> { ["RateLimiting:Enabled"] = "true" }
        };
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(
            "/v1/auth/sso/google/AUTHORIZE?redirect_uri=https%3A%2F%2Fapp.example.test&code_challenge=challenge&state=state");

        Header(response, "RateLimit-Limit").Should().Be("20");
    }

    [Fact]
    public async Task Non_rate_limited_404_paths_do_not_get_rate_limit_headers()
    {
        using var factory = new ApiFactory
        {
            Overrides = new Dictionary<string, string?> { ["RateLimiting:Enabled"] = "true" }
        };
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/v1/not-a-route/refresh");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.Contains("RateLimit-Limit").Should().BeFalse();
        response.Headers.Contains("RateLimit-Remaining").Should().BeFalse();
        response.Headers.Contains("RateLimit-Reset").Should().BeFalse();
    }

    [Fact]
    public async Task Disabled_rate_limiting_emits_no_rate_limit_headers()
    {
        using var factory = new ApiFactory
        {
            Overrides = new Dictionary<string, string?>
            {
                ["RateLimiting:Enabled"] = "false"
            }
        };
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(
            "/v1/auth/sso/google/authorize?redirect_uri=https%3A%2F%2Fapp.example.test&code_challenge=challenge&state=state");
        response.Headers.Contains("RateLimit-Limit").Should().BeFalse();
        response.Headers.Contains("RateLimit-Remaining").Should().BeFalse();
        response.Headers.Contains("RateLimit-Reset").Should().BeFalse();
    }

    private static HttpRequestMessage RateLimitRequest(string path)
    {
        if (path.EndsWith("/token", StringComparison.Ordinal))
        {
            // Malformed on purpose: the limiter counts it, and binding rejects it before the unreachable code store.
            return new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent("{", Encoding.UTF8, "application/json")
            };
        }

        if (path.EndsWith("/refresh", StringComparison.Ordinal))
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.TryAddWithoutValidation("Origin", "http://localhost:47914").Should().BeTrue();
            return request;
        }

        return new HttpRequestMessage(HttpMethod.Get, path);
    }

    private static string Header(HttpResponseMessage response, string name) => response.Headers.GetValues(name).Single();

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
