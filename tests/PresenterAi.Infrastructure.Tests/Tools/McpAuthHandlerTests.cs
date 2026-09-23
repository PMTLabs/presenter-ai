using System.Net;
using System.Text;
using PresenterAi.Infrastructure.Tools.Mcp;
using Xunit;

namespace PresenterAi.Infrastructure.Tests.Tools;

public sealed class McpAuthHandlerTests
{
    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"tools\\/call\"}", 1, 0)]
    [InlineData("[{\"method\":\"tools/list\"}]", 1, 0)]
    [InlineData("{broken", 1, 0)]
    [InlineData("{\"method\":\"tools/list\"}", 2, 1)]
    [InlineData("{\"method\":\"initialize\",\"note\":\"tools/call\"}", 2, 1)]
    public async Task Unauthorized_retries_only_a_single_structurally_safe_method(
        string body, int expectedEffects, int expectedRefreshes)
    {
        var effects = 0;
        var refreshes = 0;
        var token = new McpTokenState("old", null, _ =>
        {
            Interlocked.Increment(ref refreshes);
            return Task.FromResult(new OAuthTokenCredential("oauth", "new", null, "Bearer", null, null,
                "client", null, "none", "pre-registered", "issuer", "token", "resource"));
        });
        using var handler = new McpHttpAuthHandler("oauth", null, null, token, ct => token.RefreshAsync(ct))
        {
            InnerHandler = new CountingHandler(() => Interlocked.Increment(ref effects))
        };
        using var client = new HttpClient(handler);
        using var response = await client.PostAsync("https://example.test/mcp",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(expectedEffects, effects);
        Assert.Equal(expectedRefreshes, refreshes);
        Assert.Equal(expectedEffects == 2 ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed class CountingHandler(Action effect) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            effect();
            return Task.FromResult(new HttpResponseMessage(request.Headers.Authorization?.Parameter == "new"
                ? HttpStatusCode.OK : HttpStatusCode.Unauthorized));
        }
    }
}
