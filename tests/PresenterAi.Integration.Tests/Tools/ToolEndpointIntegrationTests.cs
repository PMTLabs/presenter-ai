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
        (await client.DeleteAsync(url + "/credential")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var list = await client.GetFromJsonAsync<ServerView[]>("/v1/tools/servers");
        list!.Single().HasCredential.Should().BeFalse();
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
        using (var scope = factory.Services.CreateScope())
        {
            var oauth = scope.ServiceProvider.GetRequiredService<PresenterAi.Infrastructure.Tools.Mcp.McpOAuthService>();
            (await oauth.RefreshAsync(owner, server.Id)).RefreshToken.Should().NotBeNull();
        }
        auth.RefreshCount.Should().BeGreaterThan(0);
        (await client.DeleteAsync($"/v1/tools/servers/{server.Id}/credential")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private IntegrationApiFactory CreateFactory(string certificateHash, string? redirectUri = null)
    {
        var factory = new IntegrationApiFactory(postgres, redis)
        {
            AllowLoopbackTools = true,
            ToolCredentialKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
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
