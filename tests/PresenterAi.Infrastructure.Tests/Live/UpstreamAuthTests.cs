using PresenterAi.Infrastructure.Live;

namespace PresenterAi.Infrastructure.Tests.Live;

public sealed class UpstreamAuthTests
{
    [Fact]
    public void Azure_gets_api_key_too_others_bearer_only()
    {
        var azure = UpstreamAuth.Headers(
            "k",
            new Uri("wss://x.services.ai.azure.com/openai/v1/live/sessions"));
        Assert.Equal("Bearer k", azure["Authorization"]);
        Assert.Equal("k", azure["api-key"]);

        var openAi = UpstreamAuth.Headers(
            "k",
            new Uri("wss://api.openai.com/v1/live/sessions"));
        Assert.Equal("Bearer k", openAi["Authorization"]);
        Assert.False(openAi.ContainsKey("api-key"));
    }
}
