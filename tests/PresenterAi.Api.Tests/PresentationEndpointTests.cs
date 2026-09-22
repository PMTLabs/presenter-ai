using System.Text.Json.Nodes;
using FluentAssertions;
using PresenterAi.Api.Tests.Infrastructure;

namespace PresenterAi.Api.Tests;

public sealed class PresentationEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("/api/presentations", "presentations.json")]
    [InlineData("/api/presentations/sample", "presentation-sample.json")]
    [InlineData("/api/presentations/ricoh-delivery-overview", "presentation-ricoh.json")]
    [InlineData("/api/config", "config.json")]
    public async Task Json_matches_node_golden(string endpoint, string golden)
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync(endpoint);
        response.EnsureSuccessStatusCode();
        var expected = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Golden", golden)));
        var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        JsonNode.DeepEquals(actual, expected).Should().BeTrue();
    }

    [Fact]
    public async Task Missing_id_is_404_with_error_body()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/presentations/nope");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"presentation \\\"nope\\\" not found\"}");
    }

    [Fact]
    public async Task Invalid_id_is_400_with_error_body()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/presentations/nope%40bad");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("invalid presentation id");
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
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/");
        response.EnsureSuccessStatusCode();
        response.Headers.GetValues("Cache-Control").Should().ContainSingle().Which.Should().Be("no-cache");
    }
}
