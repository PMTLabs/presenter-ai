using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using PresenterAi.Api.Tests.Infrastructure;

namespace PresenterAi.Api.Tests;

public sealed class PresentationEndpointTests(ApiFactory factory, WebRootFixture webRoot) :
    IClassFixture<ApiFactory>, IClassFixture<WebRootFixture>
{
    [Theory]
    [InlineData("/v1/presentations", "presentations.json")]
    [InlineData("/v1/presentations/sample", "presentation-sample.json")]
    [InlineData("/v1/presentations/ricoh-delivery-overview", "presentation-ricoh.json")]
    [InlineData("/v1/config", "config.json")]
    public async Task Json_matches_node_golden(string endpoint, string golden)
    {
        using var client = factory.CreateAuthenticatedClient();
        var response = await client.GetAsync(endpoint);
        response.EnsureSuccessStatusCode();
        var expected = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Golden", golden)));
        var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        if (endpoint == "/v1/presentations")
        {
            actual!["page"]!.GetValue<int>().Should().Be(1);
            actual["pageSize"]!.GetValue<int>().Should().Be(25);
            actual["total"]!.GetValue<int>().Should().Be(actual["items"]!.AsArray().Count);
            actual = actual["items"];
        }

        JsonNode.DeepEquals(actual, expected).Should().BeTrue();
    }

    [Fact]
    public async Task Meta_carries_max_minutes()
    {
        var root = Path.Combine(Path.GetTempPath(), "presenter-ai-max-minutes", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "presentations"));
        Directory.CreateDirectory(Path.Combine(root, "decks"));
        await File.WriteAllTextAsync(Path.Combine(root, "presentations", "limited.md"),
            "---\ndeck: decks/sample/index.html\nmaxMinutes: 35\n---\n## Slide 1\nHello.");
        try
        {
            using var client = factory.WithWebHostBuilder(builder => builder.UseSetting("Content:RootDir", root)).CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", ApiFactory.CreateTestToken("test-user", "test@presenter-ai.local"));
            var response = await client.GetAsync("/v1/presentations/limited");
            response.EnsureSuccessStatusCode();
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            body!["meta"]!["maxMinutes"]!.GetValue<int>().Should().Be(35);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_id_is_404_with_error_body()
    {
        using var client = factory.CreateAuthenticatedClient();
        var response = await client.GetAsync("/v1/presentations/nope");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("presentation.not_found");
    }

    [Fact]
    public async Task Invalid_id_is_400_with_error_body()
    {
        using var client = factory.CreateAuthenticatedClient();
        var response = await client.GetAsync("/v1/presentations/nope%40bad");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("validation.failed");
    }

    [Fact]
    public async Task Deck_404_text_matches_node()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/decks/nope/x.html");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Be("Deck not found: /nope/x.html. Put your deck under decks/<name>/.");
    }

    [Fact]
    public async Task Deck_file_is_served_from_the_content_root()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/decks/sample/index.html");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        (await response.Content.ReadAsStringAsync()).Should().Contain("<html");
    }

    [Fact]
    public async Task Static_ui_has_no_cache_header()
    {
        using var client = factory
            .WithWebHostBuilder(builder => builder.UseSetting("Content:WebRoot", webRoot.Root))
            .CreateClient();
        var response = await client.GetAsync("/");
        response.EnsureSuccessStatusCode();
        response.Headers.GetValues("Cache-Control").Should().ContainSingle().Which.Should().Be("no-cache");
    }
}
