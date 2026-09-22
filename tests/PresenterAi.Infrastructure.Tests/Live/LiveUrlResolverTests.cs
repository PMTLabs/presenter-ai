using PresenterAi.Infrastructure.Live;

namespace PresenterAi.Infrastructure.Tests.Live;

public sealed class LiveUrlResolverTests
{
    [Fact]
    public void Azure_host_maps_to_openai_v1_live_sessions()
    {
        Assert.Equal(
            "wss://x.services.ai.azure.com/openai/v1/live/sessions",
            LiveUrlResolver.Resolve("https://x.services.ai.azure.com").ToString());
        Assert.Equal(
            "wss://x.services.ai.azure.com/openai/v1/live/sessions",
            LiveUrlResolver.Resolve("https://x.services.ai.azure.com/").ToString());
    }

    [Fact]
    public void Azure_openai_host_maps_to_openai_v1_live_sessions()
    {
        Assert.Equal(
            "wss://x.openai.azure.com/openai/v1/live/sessions",
            LiveUrlResolver.Resolve("https://x.openai.azure.com").ToString());
    }

    [Fact]
    public void Openai_direct_maps_to_v1_live_sessions()
    {
        Assert.Equal(
            "wss://api.openai.com/v1/live/sessions",
            LiveUrlResolver.Resolve("https://api.openai.com").ToString());
    }

    [Fact]
    public void Gateway_host_maps_to_v1_live_sessions()
    {
        Assert.Equal(
            "wss://ai-gateway.vercel.sh/v1/live/sessions",
            LiveUrlResolver.Resolve("https://ai-gateway.vercel.sh").ToString());
    }

    [Fact]
    public void Explicit_live_sessions_path_is_kept()
    {
        Assert.Equal(
            "wss://example.com/custom/live/sessions",
            LiveUrlResolver.Resolve("wss://example.com/custom/live/sessions").ToString());
        Assert.Equal(
            "ws://127.0.0.1:4321/v1/live/sessions",
            LiveUrlResolver.Resolve("http://127.0.0.1:4321/v1/live/sessions").ToString());
    }

    [Fact]
    public void Http_maps_to_ws()
    {
        Assert.Equal(
            "ws://localhost:9000/v1/live/sessions",
            LiveUrlResolver.Resolve("http://localhost:9000").ToString());
    }

    [Fact]
    public void Invalid_endpoint_throws_with_upstream_setting_name()
    {
        var empty = Assert.Throws<InvalidOperationException>(() => LiveUrlResolver.Resolve(string.Empty));
        Assert.Equal("resolveLiveUrl: endpoint is empty", empty.Message);

        var invalid = Assert.Throws<InvalidOperationException>(() => LiveUrlResolver.Resolve("not a url"));
        Assert.Contains("Upstream:Endpoint is not a valid URL", invalid.Message);

        var unsupported = Assert.Throws<InvalidOperationException>(() => LiveUrlResolver.Resolve("ftp://x.com"));
        Assert.Contains("Upstream:Endpoint has unsupported scheme: ftp:", unsupported.Message);
    }

    [Fact]
    public void Azure_host_matching_is_case_insensitive_and_requires_suffix()
    {
        Assert.True(LiveUrlResolver.IsAzureHost("FOO.AZURE.US"));
        Assert.False(LiveUrlResolver.IsAzureHost("azure.com.evil.io"));
    }
}
