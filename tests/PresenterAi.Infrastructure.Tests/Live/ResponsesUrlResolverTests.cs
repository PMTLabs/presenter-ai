using PresenterAi.Infrastructure.Live;

namespace PresenterAi.Infrastructure.Tests.Live;

public sealed class ResponsesUrlResolverTests
{
    [Fact]
    public void Azure_live_url_maps_to_openai_v1_responses()
    {
        var live = LiveUrlResolver.Resolve("https://x.services.ai.azure.com");

        Assert.Equal("https://x.services.ai.azure.com/openai/v1/responses", ResponsesUrlResolver.Resolve(live).ToString());
    }

    [Fact]
    public void Openai_live_url_maps_to_v1_responses()
    {
        var live = LiveUrlResolver.Resolve("https://api.openai.com");

        Assert.Equal("https://api.openai.com/v1/responses", ResponsesUrlResolver.Resolve(live).ToString());
    }

    [Fact]
    public void Custom_path_prefix_is_kept_and_ws_maps_to_http()
    {
        Assert.Equal(
            "https://gateway.example.com/team/a/responses",
            ResponsesUrlResolver.Resolve(new Uri("wss://gateway.example.com/team/a/live/sessions")).ToString());
        Assert.Equal(
            "http://127.0.0.1:4321/v1/responses",
            ResponsesUrlResolver.Resolve(new Uri("ws://127.0.0.1:4321/v1/live/sessions")).ToString());
    }

    [Fact]
    public void Query_string_is_kept()
    {
        var live = LiveUrlResolver.Resolve("https://x.openai.azure.com/openai/v1/live/sessions?api-version=preview");

        Assert.Equal(
            "https://x.openai.azure.com/openai/v1/responses?api-version=preview",
            ResponsesUrlResolver.Resolve(live).ToString());
    }
}
