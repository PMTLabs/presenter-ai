using System.Net;
using System.Text.Json;
using PresenterAi.Api.Tests.Infrastructure;
using Xunit;

namespace PresenterAi.Api.Tests;

public sealed class ToolOAuthMetadataTests
{
    [Fact]
    public async Task Https_client_metadata_is_public_and_has_exact_registration_fields()
    {
        using var factory = new ApiFactory { Overrides = new Dictionary<string, string?>
        {
            ["OAuth:ApiBaseUrl"] = "https://app.example/"
        } };
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/v1/tools/oauth/client-metadata.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(6, root.EnumerateObject().Count());
        Assert.Equal("https://app.example/v1/tools/oauth/client-metadata.json", root.GetProperty("client_id").GetString());
        Assert.Equal("https://app.example/tools/oauth/callback", root.GetProperty("redirect_uris")[0].GetString());
        Assert.Equal("none", root.GetProperty("token_endpoint_auth_method").GetString());
        Assert.Equal("authorization_code", root.GetProperty("grant_types")[0].GetString());
        Assert.Equal("refresh_token", root.GetProperty("grant_types")[1].GetString());
        Assert.Equal("code", root.GetProperty("response_types")[0].GetString());
    }

    [Fact]
    public async Task Http_base_does_not_publish_unusable_client_metadata()
    {
        using var factory = new ApiFactory { Overrides = new Dictionary<string, string?>
        {
            ["OAuth:ApiBaseUrl"] = "http://localhost:47913"
        } };
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/v1/tools/oauth/client-metadata.json");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
