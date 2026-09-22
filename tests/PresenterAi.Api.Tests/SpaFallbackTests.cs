using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using PresenterAi.Api.Tests.Infrastructure;

namespace PresenterAi.Api.Tests;

public sealed class SpaFallbackTests(ApiFactory factory, WebRootFixture webRoot) :
    IClassFixture<ApiFactory>, IClassFixture<WebRootFixture>
{
    [Fact]
    public async Task Deep_link_serves_index()
    {
        using var client = factory.WithWebHostBuilder(builder => builder.UseSetting("Content:WebRoot", webRoot.Root)).CreateClient();
        var response = await client.GetAsync("/present/abc");
        response.EnsureSuccessStatusCode();
        (await response.Content.ReadAsStringAsync()).Should().Contain("Presenter");
    }

    [Fact]
    public async Task Api_unknown_route_is_404_not_index()
    {
        using var client = factory.WithWebHostBuilder(builder => builder.UseSetting("Content:WebRoot", webRoot.Root)).CreateClient();
        var response = await client.GetAsync("/api/nope");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).ToLowerInvariant().Should().NotContain("<html");
    }

    [Fact]
    public async Task Ws_without_upgrade_is_400()
    {
        using var client = factory.WithWebHostBuilder(builder => builder.UseSetting("Content:WebRoot", webRoot.Root)).CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", ApiFactory.CreateTestToken("test-user", "test@presenter-ai.local"));
        var response = await client.GetAsync("/ws");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }
}
