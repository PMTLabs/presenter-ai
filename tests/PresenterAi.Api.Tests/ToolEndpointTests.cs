using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using PresenterAi.Api.Tests.Infrastructure;
using PresenterAi.Application.Tools.External;
using PresenterAi.Contracts;
using PresenterAi.Contracts.Tools;
using Xunit;

namespace PresenterAi.Api.Tests;

public sealed class ToolEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly HashSet<string> ServerViewAllowedProperties = new(StringComparer.Ordinal)
    {
        "id", "name", "url", "authKind", "status", "lastErrorCode", "alwaysAsk", "hasCredential", "lastConnectedAt"
    };

    private static readonly HashSet<string> ForbiddenSecretProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "headerValue", "headerName", "credential", "secret", "accessToken", "refreshToken", "token", "password", "key"
    };

    [Theory]
    [InlineData("GET", "/v1/tools/settings")]
    [InlineData("PUT", "/v1/tools/settings")]
    [InlineData("GET", "/v1/tools/servers")]
    [InlineData("POST", "/v1/tools/servers")]
    [InlineData("PATCH", "/v1/tools/servers/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/v1/tools/servers/00000000-0000-0000-0000-000000000001")]
    [InlineData("PUT", "/v1/tools/servers/00000000-0000-0000-0000-000000000001/credential")]
    [InlineData("DELETE", "/v1/tools/servers/00000000-0000-0000-0000-000000000001/credential")]
    [InlineData("POST", "/v1/tools/servers/00000000-0000-0000-0000-000000000001/oauth/start")]
    [InlineData("POST", "/v1/tools/oauth/complete")]
    [InlineData("POST", "/v1/tools/servers/00000000-0000-0000-0000-000000000001/test")]
    [InlineData("GET", "/v1/tools/servers/00000000-0000-0000-0000-000000000001/tools")]
    [InlineData("PUT", "/v1/tools/servers/00000000-0000-0000-0000-000000000001/tools/search")]
    public async Task Every_route_requires_sign_in(string method, string path)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "PUT" or "POST" or "PATCH")
        {
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["code"]?.GetValue<string>().Should().Be(ErrorCodes.AuthRequired);
    }

    [Fact]
    public async Task Client_metadata_document_is_anonymous()
    {
        using var customFactory = new ApiFactory
        {
            Overrides = new Dictionary<string, string?> { ["OAuth:ApiBaseUrl"] = "https://app.example/" }
        };
        using var client = customFactory.CreateClient();
        using var response = await client.GetAsync("/v1/tools/oauth/client-metadata.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["client_name"]?.GetValue<string>().Should().Be("Presenter AI");
    }

    [Theory]
    [InlineData("PATCH", "/v1/tools/servers/{id}", "{\"name\":\"new-name\"}")]
    [InlineData("DELETE", "/v1/tools/servers/{id}", null)]
    [InlineData("PUT", "/v1/tools/servers/{id}/credential", "{\"headerName\":\"Authorization\",\"headerValue\":\"Bearer token\"}")]
    [InlineData("DELETE", "/v1/tools/servers/{id}/credential", null)]
    [InlineData("POST", "/v1/tools/servers/{id}/oauth/start", "{}")]
    [InlineData("POST", "/v1/tools/servers/{id}/test", null)]
    [InlineData("GET", "/v1/tools/servers/{id}/tools", null)]
    [InlineData("PUT", "/v1/tools/servers/{id}/tools/search", "{\"alwaysAsk\":true}")]
    public async Task Another_users_server_id_returns_404_on_every_route(string method, string pathTemplate, string? body)
    {
        factory.ToolRepository.Clear();
        var serverId = Guid.NewGuid();
        var userAServer = new ToolConnection(
            serverId, "user-a", "Server A", "server-a", "https://93.184.216.34/mcp",
            "none", "connected", null, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
        factory.ToolRepository.Seed(userAServer);

        using var clientB = factory.CreateAuthenticatedClient("user-b");
        var path = pathTemplate.Replace("{id}", serverId.ToString());
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null)
        {
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        }

        using var response = await clientB.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["code"]?.GetValue<string>().Should().Be(ErrorCodes.ToolsServerNotFound);
        if (method == "PATCH")
        {
            var original = await factory.ToolRepository.GetAsync("user-a", serverId);
            original!.Name.Should().Be("Server A", "a rejected cross-owner patch must have no side effects");
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345678901234567890123456789012345678901")] // 41 chars
    public async Task Create_server_with_invalid_name_returns_tools_name_invalid(string name)
    {
        using var client = factory.CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync("/v1/tools/servers", new CreateToolServerRequest(name, "https://93.184.216.34/mcp"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["code"]?.GetValue<string>().Should().Be(ErrorCodes.ToolsNameInvalid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("http://93.184.216.34/mcp")] // http is refused
    [InlineData("https://user:pass@93.184.216.34/mcp")] // user info refused
    [InlineData("https://93.184.216.34/mcp#fragment")] // fragment refused
    public async Task Create_server_with_invalid_url_returns_tools_url_invalid(string url)
    {
        using var client = factory.CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync("/v1/tools/servers", new CreateToolServerRequest("My Server", url));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["code"]?.GetValue<string>().Should().Be(ErrorCodes.ToolsUrlInvalid);
    }

    [Theory]
    [InlineData("https://127.0.0.1/mcp")] // loopback
    [InlineData("https://169.254.169.254/mcp")] // cloud metadata
    [InlineData("https://10.0.0.1/mcp")] // private
    [InlineData("https://192.168.1.1/mcp")] // private
    public async Task Create_server_with_blocked_url_returns_tools_url_blocked(string url)
    {
        using var client = factory.CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync("/v1/tools/servers", new CreateToolServerRequest("My Server", url));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["code"]?.GetValue<string>().Should().Be(ErrorCodes.ToolsUrlBlocked);
    }

    [Fact]
    public async Task Create_server_returns_tools_server_limit_when_10_servers_exist()
    {
        factory.ToolRepository.Clear();
        using var client = factory.CreateAuthenticatedClient("user-limit-test");

        for (var i = 1; i <= 10; i++)
        {
            var server = new ToolConnection(
                Guid.NewGuid(), "user-limit-test", $"Server {i}", $"server-{i}", "https://93.184.216.34/mcp",
                "none", "connected", null, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
            factory.ToolRepository.Seed(server);
        }

        var response = await client.PostAsJsonAsync("/v1/tools/servers", new CreateToolServerRequest("Server 11", "https://93.184.216.34/mcp"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["code"]?.GetValue<string>().Should().Be(ErrorCodes.ToolsServerLimit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345678901234567890123456789012345678901")]
    public async Task Update_server_with_invalid_name_returns_tools_name_invalid(string name)
    {
        factory.ToolRepository.Clear();
        var serverId = Guid.NewGuid();
        factory.ToolRepository.Seed(new ToolConnection(
            serverId, "test-user", "Initial Name", "initial", "https://93.184.216.34/mcp",
            "none", "connected", null, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));

        using var client = factory.CreateAuthenticatedClient("test-user");
        var response = await client.PatchAsJsonAsync($"/v1/tools/servers/{serverId}", new UpdateToolServerRequest(Name: name));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["code"]?.GetValue<string>().Should().Be(ErrorCodes.ToolsNameInvalid);
    }

    [Theory]
    [InlineData("Host", "example.com")] // forbidden Host header
    [InlineData("Content-Length", "100")] // forbidden
    [InlineData("Transfer-Encoding", "chunked")] // forbidden
    [InlineData("Connection", "keep-alive")] // forbidden
    [InlineData("Cookie", "session=123")] // forbidden
    [InlineData("Mcp-Secret", "secret")] // forbidden prefix
    [InlineData("mcp-custom", "value")] // forbidden prefix case-insensitive
    [InlineData("Bad Header Name", "value")] // spaces not allowed in RFC 7230 token
    [InlineData("Header@Name", "value")] // '@' not allowed in RFC 7230 token
    [InlineData("Header:Name", "value")] // ':' not allowed
    [InlineData("X-Custom", "value\r\ninjected: true")] // CR/LF injection
    public async Task Save_credential_with_invalid_header_returns_tools_header_invalid(string headerName, string headerValue)
    {
        factory.ToolRepository.Clear();
        var serverId = Guid.NewGuid();
        factory.ToolRepository.Seed(new ToolConnection(
            serverId, "test-user", "Server", "server", "https://93.184.216.34/mcp",
            "none", "connected", null, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));

        using var client = factory.CreateAuthenticatedClient("test-user");
        var response = await client.PutAsJsonAsync($"/v1/tools/servers/{serverId}/credential",
            new SaveToolCredentialRequest(headerName, headerValue));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["code"]?.GetValue<string>().Should().Be(ErrorCodes.ToolsHeaderInvalid);
    }

    [Fact]
    public async Task Save_credential_with_oversize_header_value_returns_tools_header_invalid()
    {
        factory.ToolRepository.Clear();
        var serverId = Guid.NewGuid();
        factory.ToolRepository.Seed(new ToolConnection(
            serverId, "test-user", "Server", "server", "https://93.184.216.34/mcp",
            "none", "connected", null, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));

        using var client = factory.CreateAuthenticatedClient("test-user");
        var hugeValue = new string('A', 4097); // > 4096 bytes
        var response = await client.PutAsJsonAsync($"/v1/tools/servers/{serverId}/credential",
            new SaveToolCredentialRequest("X-Api-Key", hugeValue));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["code"]?.GetValue<string>().Should().Be(ErrorCodes.ToolsHeaderInvalid);
    }

    [Fact]
    public async Task Operations_requiring_credentials_key_return_tools_credentials_unavailable_when_key_missing()
    {
        using var missingKeyFactory = new ApiFactory
        {
            Overrides = new Dictionary<string, string?> { ["Tools:CredentialKey"] = "" }
        };
        var serverId = Guid.NewGuid();
        missingKeyFactory.ToolRepository.Seed(new ToolConnection(
            serverId, "test-user", "Server", "server", "https://93.184.216.34/mcp",
            "none", "connected", null, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));

        using var client = missingKeyFactory.CreateAuthenticatedClient("test-user");

        // 1. Save credential
        var credResp = await client.PutAsJsonAsync($"/v1/tools/servers/{serverId}/credential",
            new SaveToolCredentialRequest("X-Api-Key", "my-key"));
        credResp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await credResp.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ToolsCredentialsUnavailable);

        // 2. Start OAuth
        var startResp = await client.PostAsJsonAsync($"/v1/tools/servers/{serverId}/oauth/start", new StartToolOAuthRequest());
        startResp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await startResp.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ToolsCredentialsUnavailable);

        // 3. Complete OAuth
        var completeResp = await client.PostAsJsonAsync("/v1/tools/oauth/complete",
            new CompleteToolOAuthRequest("code123", "state123"));
        completeResp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await completeResp.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ToolsCredentialsUnavailable);
    }

    [Theory]
    [InlineData("", "state123")]
    [InlineData("   ", "state123")]
    [InlineData("code123", "")]
    [InlineData("code123", "   ")]
    public async Task Complete_oauth_with_empty_parameters_returns_tools_oauth_state_invalid(string code, string state)
    {
        using var client = factory.CreateAuthenticatedClient("test-user");
        var response = await client.PostAsJsonAsync("/v1/tools/oauth/complete",
            new CompleteToolOAuthRequest(code, state));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["code"]?.GetValue<string>().Should().Be(ErrorCodes.ToolsOAuthStateInvalid);
    }

    [Fact]
    public async Task ServerView_response_matches_property_allowlist_and_exposes_no_secrets()
    {
        factory.ToolRepository.Clear();
        var serverId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var connection = new ToolConnection(
            serverId, "user-allowlist", "Allowlist Test Server", "allowlist-test", "https://93.184.216.34/mcp",
            "header", "connected", null, true, now, now, now, true);
        factory.ToolRepository.Seed(connection, new ToolCredential([1, 2, 3], "k1", null, 1));

        using var client = factory.CreateAuthenticatedClient("user-allowlist");

        // Test GET /servers
        var listResp = await client.GetAsync("/v1/tools/servers");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var listJson = JsonNode.Parse(await listResp.Content.ReadAsStringAsync())!.AsArray();
        listJson.Should().HaveCount(1);
        var serverObj = listJson[0]!.AsObject();

        AssertServerViewProperties(serverObj);

        // Test PATCH /servers/{id}
        var patchResp = await client.PatchAsJsonAsync($"/v1/tools/servers/{serverId}",
            new UpdateToolServerRequest(Name: "Renamed Server"));
        patchResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var patchObj = JsonNode.Parse(await patchResp.Content.ReadAsStringAsync())!.AsObject();
        AssertServerViewProperties(patchObj);

        // Test POST /servers (create)
        var createResp = await client.PostAsJsonAsync("/v1/tools/servers",
            new CreateToolServerRequest("Created Server", "https://93.184.216.34/mcp"));
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var createObj = JsonNode.Parse(await createResp.Content.ReadAsStringAsync())!.AsObject();
        AssertServerViewProperties(createObj);
    }

    [Fact]
    public async Task Saving_a_header_does_not_return_its_value_or_name()
    {
        factory.ToolRepository.Clear();
        var id = Guid.NewGuid();
        factory.ToolRepository.Seed(new ToolConnection(id, "header-owner", "Safe", "safe", "https://127.0.0.1/mcp",
            "none", "not_connected", null, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));
        using var client = factory.CreateAuthenticatedClient("header-owner");
        var sentinel = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        using var response = await client.PutAsJsonAsync($"/v1/tools/servers/{id}/credential",
            new SaveToolCredentialRequest("X-Custom", sentinel));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(sentinel).And.NotContain("X-Custom");
        AssertServerViewProperties(JsonNode.Parse(body)!.AsObject());
    }

    [Fact]
    public async Task ToolSettingsResponse_matches_property_allowlist()
    {
        using var client = factory.CreateAuthenticatedClient("settings-user");

        // GET /settings
        var getResp = await client.GetAsync("/v1/tools/settings");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var getObj = JsonNode.Parse(await getResp.Content.ReadAsStringAsync())!.AsObject();
        getObj.Select(p => p.Key).Should().BeEquivalentTo(["webSearchEnabled"]);

        // PUT /settings
        var putResp = await client.PutAsJsonAsync("/v1/tools/settings", new UpdateToolSettingsRequest(true));
        putResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var putObj = JsonNode.Parse(await putResp.Content.ReadAsStringAsync())!.AsObject();
        putObj.Select(p => p.Key).Should().BeEquivalentTo(["webSearchEnabled"]);
        putObj["webSearchEnabled"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task TestToolServerResponse_matches_property_allowlist()
    {
        factory.ToolRepository.Clear();
        var serverId = Guid.NewGuid();
        factory.ToolRepository.Seed(new ToolConnection(
            serverId, "test-user", "Test Server", "test-server", "https://127.0.0.1/mcp",
            "none", "not_connected", null, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));

        using var client = factory.CreateAuthenticatedClient("test-user");
        var resp = await client.PostAsync($"/v1/tools/servers/{serverId}/test", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var obj = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!.AsObject();

        var allowedTestProps = new HashSet<string> { "ok", "toolCount", "errorCode" };
        foreach (var prop in obj)
        {
            allowedTestProps.Should().Contain(prop.Key);
        }
        foreach (var forbidden in ForbiddenSecretProperties)
        {
            obj.Should().NotContainKey(forbidden);
        }
    }

    [Fact]
    public async Task Rate_limiting_enforces_30_per_minute_per_user_on_the_6_external_routes()
    {
        using var rateFactory = new ApiFactory
        {
            Overrides = new Dictionary<string, string?>
            {
                ["RateLimiting:Enabled"] = "true",
                ["Tools:CredentialKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            }
        };

        var serverId = Guid.NewGuid();
        var user1Server = new ToolConnection(
            serverId, "user-rate-1", "Server", "server", "https://127.0.0.1/mcp",
            "none", "connected", null, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
        rateFactory.ToolRepository.Seed(user1Server);

        using var client1 = rateFactory.CreateAuthenticatedClient("user-rate-1");
        using var client2 = rateFactory.CreateAuthenticatedClient("user-rate-2");

        // The 6 external endpoints:
        // 1. POST /v1/tools/servers (create)
        // 2. POST /v1/tools/servers/{id}/test (test)
        // 3. GET /v1/tools/servers/{id}/tools (tools)
        // 4. PUT /v1/tools/servers/{id}/credential (credential)
        // 5. POST /v1/tools/servers/{id}/oauth/start (oauth start)
        // 6. POST /v1/tools/oauth/complete (oauth complete)
        //
        // Send 30 requests from client1 on a rate-limited endpoint (e.g. POST /v1/tools/servers/{id}/test):
        for (var req = 1; req <= 30; req++)
        {
            using var response = await client1.PostAsync($"/v1/tools/servers/{serverId}/test", null);
            response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, $"request {req} should not be rate limited");
        }

        // 31st request from client1 should be rejected with 429
        using var rejected = await client1.PostAsync($"/v1/tools/servers/{serverId}/test", null);
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var json = JsonNode.Parse(await rejected.Content.ReadAsStringAsync())!;
        json["code"]?.GetValue<string>().Should().Be(ErrorCodes.RateLimitExceeded);

        rejected.Headers.GetValues("RateLimit-Limit").Should().ContainSingle().Which.Should().Be("30");
        rejected.Headers.GetValues("RateLimit-Remaining").Should().ContainSingle().Which.Should().Be("0");
        rejected.Headers.Contains("RateLimit-Reset").Should().BeTrue();
        rejected.Headers.RetryAfter.Should().NotBeNull();

        var limitedRoutes = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Post, "/v1/tools/servers", new CreateToolServerRequest("Extra", "https://93.184.216.34/mcp")),
            (HttpMethod.Post, $"/v1/tools/servers/{serverId}/test", null),
            (HttpMethod.Get, $"/v1/tools/servers/{serverId}/tools", null),
            (HttpMethod.Put, $"/v1/tools/servers/{serverId}/credential", new SaveToolCredentialRequest("X-Key", "value")),
            (HttpMethod.Post, $"/v1/tools/servers/{serverId}/oauth/start", new StartToolOAuthRequest()),
            (HttpMethod.Post, "/v1/tools/oauth/complete", new CompleteToolOAuthRequest("code", "state"))
        };
        foreach (var (method, path, body) in limitedRoutes)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await client1.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, $"{method} {path} shares the tools limit");
        }

        // Meanwhile, client2 (different user) is NOT rate limited:
        var user2Server = new ToolConnection(
            Guid.NewGuid(), "user-rate-2", "Server 2", "server-2", "https://127.0.0.1/mcp",
            "none", "connected", null, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
        rateFactory.ToolRepository.Seed(user2Server);

        using var client2Response = await client2.PostAsync($"/v1/tools/servers/{user2Server.Id}/test", null);
        client2Response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
    }

    private static void AssertServerViewProperties(JsonObject serverObj)
    {
        var keys = serverObj.Select(p => p.Key).ToList();
        keys.Should().BeEquivalentTo(ServerViewAllowedProperties);

        foreach (var forbidden in ForbiddenSecretProperties)
        {
            serverObj.Should().NotContainKey(forbidden);
        }
    }
}
