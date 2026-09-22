using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using PresenterAi.Api.Tests.Infrastructure;

namespace PresenterAi.Api.Tests;

public sealed class WebRootFixture : IDisposable
{
    public WebRootFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), $"presenter-ai-web-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "index.html"),
            "<html><body>Presenter<div id=\"root\"></div></body></html>");
    }

    public string Root { get; }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

public sealed class WebRootTests(ApiFactory factory, WebRootFixture webRoot) :
    IClassFixture<ApiFactory>, IClassFixture<WebRootFixture>
{
    [Fact]
    public async Task Present_web_root_serves_index_and_deep_links_without_cache()
    {
        using var client = CreateClient(webRoot.Root);

        // "/" and "/present/sample" go through the SPA fallback; "/index.html" is served by the static-file
        // middleware — both paths must send no-cache so a rebuilt app is never served stale.
        foreach (var path in new[] { "/", "/present/sample", "/index.html" })
        {
            var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, path);
            response.Content.Headers.ContentType!.MediaType.Should().Be("text/html", path);
            response.Headers.CacheControl.Should().NotBeNull(path);
            response.Headers.CacheControl!.NoCache.Should().BeTrue(path);
            (await response.Content.ReadAsStringAsync()).Should().Contain("Presenter", path);
        }
    }

    [Fact]
    public async Task Missing_web_root_is_api_only()
    {
        var missingRoot = Path.Combine(Path.GetTempPath(), $"presenter-ai-missing-{Guid.NewGuid():N}");
        using var client = CreateClient(missingRoot);

        (await client.GetAsync("/")).StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        (await client.GetAsync("/present/x")).StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        (await client.GetAsync("/health")).StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var deckResponse = await client.GetAsync("/decks/sample/index.html");
        deckResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var apiResponse = await client.GetAsync("/api/nope");
        apiResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        (await apiResponse.Content.ReadAsStringAsync()).ToLowerInvariant().Should().NotContain("<html");
    }

    private HttpClient CreateClient(string root) =>
        factory.WithWebHostBuilder(builder => builder.UseSetting("Content:WebRoot", root)).CreateClient();
}
