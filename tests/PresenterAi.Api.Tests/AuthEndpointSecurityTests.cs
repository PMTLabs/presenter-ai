using System.Net;
using System.Security.Cryptography;
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

    [Fact]
    public async Task Rate_limited_routes_emit_truthful_rate_limit_headers_and_retry_after()
    {
        using var factory = new ApiFactory
        {
            Overrides = new Dictionary<string, string?>
            {
                ["RateLimiting:Enabled"] = "true",
                ["OAuth:ApiBaseUrl"] = "https://api.example.test",
                ["OAuth:StateEncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["OAuth:AllowedRedirectUris:0"] = "https://app.example.test",
                ["OAuth:Google:Enabled"] = "true",
                ["OAuth:Google:ClientId"] = "client",
                ["OAuth:Google:ClientSecret"] = "test-only-secret",
                ["OAuth:Google:AuthorizationEndpoint"] = "https://identity.example.test/authorize",
                ["OAuth:Google:TokenEndpoint"] = "https://identity.example.test/token",
                ["OAuth:Google:UserInfoEndpoint"] = "https://identity.example.test/userinfo"
            }
        };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var url = "/v1/auth/sso/google/authorize?redirect_uri=https%3A%2F%2Fapp.example.test&code_challenge=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa&state=state";
        using var first = await client.GetAsync(url);
        using var second = await client.GetAsync(url);
        first.StatusCode.Should().Be(HttpStatusCode.Found);
        second.StatusCode.Should().Be(HttpStatusCode.Found);
        Header(first, "RateLimit-Limit").Should().Be("20");
        first.Headers.Contains("RateLimit-Remaining").Should().BeFalse();
        first.Headers.Contains("RateLimit-Reset").Should().BeFalse();
        Header(second, "RateLimit-Limit").Should().Be("20");
        second.Headers.Contains("RateLimit-Remaining").Should().BeFalse();
        second.Headers.Contains("RateLimit-Reset").Should().BeFalse();

        HttpResponseMessage? last = null;
        for (var index = 0; index < 19; index++)
        {
            last?.Dispose();
            last = await client.GetAsync(url);
        }

        last!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        Header(last, "RateLimit-Remaining").Should().Be("0");
        var reset = Header(last, "RateLimit-Reset");
        var retryAfter = last.Headers.RetryAfter!.Delta!.Value.TotalSeconds;
        retryAfter.Should().BeInRange(1, 60);
        retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture).Should().Be(reset);
        last.Dispose();
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
