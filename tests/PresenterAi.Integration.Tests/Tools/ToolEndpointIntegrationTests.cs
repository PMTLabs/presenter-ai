using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Net.Security;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Contracts.Tools;
using PresenterAi.Infrastructure.Persistence;
using PresenterAi.Infrastructure.Persistence.Entities;
using PresenterAi.Infrastructure.Tests.Tools;
using PresenterAi.Integration.Tests.Support;
using StackExchange.Redis;
using Xunit;

namespace PresenterAi.Integration.Tests.Tools;

[Collection(IntegrationCollection.Name)]
public sealed class ToolEndpointIntegrationTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task Credential_test_tools_and_disconnect_use_persisted_rows()
    {
        await using var mcp = await TestMcpServer.StartAsync();
        using var factory = CreateFactory(mcp.Certificate.GetCertHashString());
        var owner = await AddUserAsync(factory);
        using var client = factory.CreateAuthenticatedClient(owner);
        var created = await client.PostAsJsonAsync("/v1/tools/servers", new CreateToolServerRequest("Fixture", mcp.Endpoint));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var server = await created.Content.ReadFromJsonAsync<ServerView>();
        server.Should().NotBeNull();
        var url = $"/v1/tools/servers/{server!.Id}";
        using (var noAuth = await client.PostAsJsonAsync(url + "/oauth/start", new StartToolOAuthRequest()))
        {
            noAuth.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonNode.Parse(await noAuth.Content.ReadAsStringAsync())!.AsObject();
            json.Select(p => p.Key).Should().BeEquivalentTo(["server"]);
            json["server"]!.AsObject().Select(p => p.Key).Should().BeEquivalentTo(
                ["id", "name", "url", "authKind", "status", "lastErrorCode", "alwaysAsk", "hasCredential", "lastConnectedAt"]);
        }
        var saved = await client.PutAsJsonAsync(url + "/credential", new SaveToolCredentialRequest("X-Api-Key", "integration-only-credential"));
        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        (await saved.Content.ReadFromJsonAsync<ServerView>())!.HasCredential.Should().BeTrue();
        var test = await client.PostAsync(url + "/test", null);
        (await test.Content.ReadFromJsonAsync<TestToolServerResponse>())!.Ok.Should().BeTrue();
        using var toolsResponse = await client.GetAsync(url + "/tools");
        var tools = JsonNode.Parse(await toolsResponse.Content.ReadAsStringAsync())!.AsArray();
        tools.Should().NotBeEmpty();
        foreach (var tool in tools)
            tool!.AsObject().Select(p => p.Key).Should().BeEquivalentTo(
                ["name", "title", "description", "readOnly", "alwaysAsk"]);
        var readOnly = tools.First(t => t!["readOnly"]!.GetValue<bool>())!["name"]!.GetValue<string>();
        (await client.PutAsJsonAsync(url + "/tools/" + readOnly, new UpdateToolOverrideRequest(false)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.PatchAsJsonAsync(url, new UpdateToolServerRequest(AlwaysAsk: true)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var effective = JsonNode.Parse(await (await client.GetAsync(url + "/tools")).Content.ReadAsStringAsync())!.AsArray();
        effective.Single(t => t!["name"]!.GetValue<string>() == readOnly)!["alwaysAsk"]!.GetValue<bool>()
            .Should().BeTrue("server always-ask cannot be masked by a saved false tool override");
        (await client.DeleteAsync(url + "/credential")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var list = await client.GetFromJsonAsync<ServerView[]>("/v1/tools/servers");
        list!.Single().HasCredential.Should().BeFalse();
    }

    [Fact]
    public async Task Changed_credential_key_keeps_reconnect_status_on_test_and_list()
    {
        await using var mcp = await TestMcpServer.StartAsync();
        using var factory = CreateFactory(mcp.Certificate.GetCertHashString());
        var owner = await AddUserAsync(factory);
        using var client = factory.CreateAuthenticatedClient(owner);
        var created = await client.PostAsJsonAsync("/v1/tools/servers", new CreateToolServerRequest("Protected", mcp.Endpoint));
        var server = (await created.Content.ReadFromJsonAsync<ServerView>())!;
        var path = $"/v1/tools/servers/{server.Id}";
        (await client.PutAsJsonAsync(path + "/credential", new SaveToolCredentialRequest("X-Api-Key", "fixture-value")))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PresenterAiDbContext>();
            await db.ToolServerCredentials.Where(row => row.ServerId == server.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.KeyId, "wrong-key-id"));
        }
        var tested = (await (await client.PostAsync(path + "/test", null))
            .Content.ReadFromJsonAsync<TestToolServerResponse>())!;
        tested.ErrorCode.Should().Be("credential_key_changed");
        var afterTest = (await client.GetFromJsonAsync<ServerView[]>("/v1/tools/servers"))!.Single();
        afterTest.Status.Should().Be("needs_reconnect");
        using var listed = await client.GetAsync(path + "/tools");
        listed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        JsonNode.Parse(await listed.Content.ReadAsStringAsync())!["code"]!.GetValue<string>()
            .Should().Be("tools_credential_key_changed");
        (await client.GetFromJsonAsync<ServerView[]>("/v1/tools/servers"))!.Single().Status
            .Should().Be("needs_reconnect");
    }

    [Fact]
    public async Task OAuth_start_connects_a_no_auth_server_without_credential_key()
    {
        await using var mcp = await TestMcpServer.StartAsync();
        using var factory = CreateFactory(mcp.Certificate.GetCertHashString(), noCredentialKey: true);
        var owner = await AddUserAsync(factory);
        using var client = factory.CreateAuthenticatedClient(owner);
        var created = await client.PostAsJsonAsync("/v1/tools/servers", new CreateToolServerRequest("Public", mcp.Endpoint));
        var server = (await created.Content.ReadFromJsonAsync<ServerView>())!;
        using var result = await client.PostAsJsonAsync($"/v1/tools/servers/{server.Id}/oauth/start", new StartToolOAuthRequest());
        result.StatusCode.Should().Be(HttpStatusCode.OK);
        var connected = (await result.Content.ReadFromJsonAsync<ToolOAuthStartResponse>())!;
        connected.Server!.Status.Should().Be("connected");
        connected.Server.AuthKind.Should().Be("none");
    }

    [Fact]
    public async Task OAuth_challenge_without_credential_key_returns_503_without_creating_state()
    {
        await using var auth = await StrictFakeAuthServer.StartAsync();
        using var factory = CreateFactory(auth.Certificate.GetCertHashString(), auth.RedirectUri, noCredentialKey: true);
        var owner = await AddUserAsync(factory);
        using var client = factory.CreateAuthenticatedClient(owner);
        var created = await client.PostAsJsonAsync("/v1/tools/servers", new CreateToolServerRequest("OAuth", auth.Resource));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var server = (await created.Content.ReadFromJsonAsync<ServerView>())!;
        var redisConnection = factory.Services.GetRequiredService<IConnectionMultiplexer>();
        var redisServer = redisConnection.GetServer(redisConnection.GetEndPoints().First());
        var stateCountBefore = redisServer.Keys(pattern: "mcp:oauth:*").Count();

        using var response = await client.PostAsJsonAsync($"/v1/tools/servers/{server.Id}/oauth/start",
            new StartToolOAuthRequest());

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["code"]!.GetValue<string>().Should().Be("tools_credentials_unavailable");
        body.AsObject().Should().NotContainKey("authorizationUrl");
        redisServer.Keys(pattern: "mcp:oauth:*").Count().Should().Be(stateCountBefore);
        auth.RegistrationCount.Should().Be(0);
        auth.AuthorizeCount.Should().Be(0);
        auth.Challenges.Should().ContainSingle("the server must have been probed before the key check");
    }

    [Fact]
    public async Task OAuth_start_complete_refresh_and_disconnect_use_real_redis_and_postgres()
    {
        await using var auth = await StrictFakeAuthServer.StartAsync();
        using var factory = CreateFactory(auth.Certificate.GetCertHashString(), auth.RedirectUri);
        var owner = await AddUserAsync(factory);
        using var client = factory.CreateAuthenticatedClient(owner);
        var created = await client.PostAsJsonAsync("/v1/tools/servers", new CreateToolServerRequest("OAuth fixture", auth.Resource));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var server = (await created.Content.ReadFromJsonAsync<ServerView>())!;
        var start = await client.PostAsJsonAsync($"/v1/tools/servers/{server.Id}/oauth/start", new StartToolOAuthRequest());
        start.StatusCode.Should().Be(HttpStatusCode.OK);
        var challenge = (await start.Content.ReadFromJsonAsync<ToolOAuthStartResponse>())!;
        challenge.AuthorizationUrl.Should().NotBeNull();
        JsonNode.Parse(await start.Content.ReadAsStringAsync())!.AsObject().Select(p => p.Key)
            .Should().BeEquivalentTo(["authorizationUrl"]);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.GetCertHashString() == auth.Certificate.GetCertHashString() };
        using var browser = new HttpClient(handler);
        using var approval = await browser.GetAsync(challenge.AuthorizationUrl);
        approval.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var query = QueryHelpers.ParseQuery(approval.Headers.Location!.Query);
        var complete = await client.PostAsJsonAsync("/v1/tools/oauth/complete",
            new CompleteToolOAuthRequest(query["code"].ToString(), query["state"].ToString(), query["iss"].ToString()));
        complete.StatusCode.Should().Be(HttpStatusCode.OK);
        (await complete.Content.ReadFromJsonAsync<ServerView>())!.HasCredential.Should().BeTrue();
        auth.ToolsListCount.Should().Be(1);
        using (var scope = factory.Services.CreateScope())
        {
            var oauth = scope.ServiceProvider.GetRequiredService<PresenterAi.Infrastructure.Tools.Mcp.McpOAuthService>();
            (await oauth.RefreshAsync(owner, server.Id)).RefreshToken.Should().NotBeNull();
        }
        auth.RefreshCount.Should().BeGreaterThan(0);
        (await client.DeleteAsync($"/v1/tools/servers/{server.Id}/credential")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private IntegrationApiFactory CreateFactory(string certificateHash, string? redirectUri = null, bool noCredentialKey = false)
    {
        var factory = new IntegrationApiFactory(postgres, redis)
        {
            AllowLoopbackTools = true,
            ToolCredentialKey = noCredentialKey ? "" : Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ToolRedirectUri = redirectUri
        };
        factory.Services.GetRequiredService<SocketsHttpHandler>().SslOptions.RemoteCertificateValidationCallback =
            (_, certificate, _, _) => certificate?.GetCertHashString() == certificateHash;
        return factory;
    }

    private static async Task<string> AddUserAsync(IntegrationApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PresenterAiDbContext>();
        await db.Database.MigrateAsync();
        var user = new User { Email = $"{Guid.NewGuid():N}@example.test", AuthMethod = "test",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }
}
