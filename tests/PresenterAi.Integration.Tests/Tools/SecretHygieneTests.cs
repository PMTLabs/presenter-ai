using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.TestHost;
using PresenterAi.Application.Auth;
using PresenterAi.Application.Tools.External;
using PresenterAi.Contracts.Tools;
using PresenterAi.Infrastructure.Persistence;
using PresenterAi.Infrastructure.Persistence.Entities;
using PresenterAi.Infrastructure.Tests.Tools;
using PresenterAi.Infrastructure.Tools.Mcp;
using PresenterAi.Integration.Tests.Support;
using StackExchange.Redis;
using Xunit;

namespace PresenterAi.Integration.Tests.Tools;

[Collection(IntegrationCollection.Name)]
public sealed class SecretHygieneTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task Every_secret_stays_out_of_all_public_surfaces_and_server_logs()
    {
        static string Sentinel() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var header = Sentinel();
        var access = Sentinel();
        var refresh = Sentinel();
        var clientSecret = Sentinel();
        var code = Sentinel();
        // PKCE and state are generated independently by the production service, not the fixture.
        var logger = new CaptureProvider();
        await using var mcp = await TestMcpServer.StartAsync();
        await using var auth = await StrictFakeAuthServer.StartAsync();
        auth.ClientMetadata = false;
        auth.RegisteredAuthMethod = "client_secret_post";
        auth.RegistrationSecret = clientSecret;
        auth.AuthorizationCode = code;
        auth.AccessToken = access;
        auth.RefreshToken = refresh;
        var hashes = new[] { mcp.Certificate.GetCertHashString(), auth.Certificate.GetCertHashString() };
        using var factory = new IntegrationApiFactory(postgres, redis)
        {
            AllowLoopbackTools = true,
            ToolCredentialKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ToolRedirectUri = auth.RedirectUri,
            ToolLogSink = logger
        };
        // Resolve a logger through the app container to prove the sink is wired before sending requests.
        var logFactory = factory.Services.GetRequiredService<ILoggerFactory>();
        logFactory.CreateLogger("hygiene.capture.probe").LogWarning("capture active");
        logger.Messages.Should().Contain(message => message.Contains("capture active", StringComparison.Ordinal));
        factory.Services.GetRequiredService<SocketsHttpHandler>().SslOptions.RemoteCertificateValidationCallback =
            (_, cert, _, _) => cert is not null && hashes.Contains(cert.GetCertHashString());
        string owner;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PresenterAiDbContext>();
            await db.Database.MigrateAsync();
            var user = new User { Email = $"{Guid.NewGuid():N}@example.test", AuthMethod = "test",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            owner = user.Id;
        }
        using var client = factory.CreateAuthenticatedClient(owner);
        var responses = new List<(HttpResponseMessage Response, bool OAuthStart)>();
        async Task<HttpResponseMessage> Track(Task<HttpResponseMessage> task, bool start = false)
        {
            var response = await task;
            responses.Add((response, start));
            return response;
        }
        try
        {
            var created = await Track(client.PostAsJsonAsync("/v1/tools/servers", new CreateToolServerRequest("Header", mcp.Endpoint)));
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            var headerId = (await created.Content.ReadFromJsonAsync<ServerView>())!.Id;
            var url = $"/v1/tools/servers/{headerId}";
            var saved = await Track(client.PutAsJsonAsync(url + "/credential", new SaveToolCredentialRequest("X-Test-Credential", header)));
            saved.StatusCode.Should().Be(HttpStatusCode.OK);
            (await saved.Content.ReadFromJsonAsync<ServerView>())!.HasCredential.Should().BeTrue();
            var test = await Track(client.PostAsync(url + "/test", null));
            (await test.Content.ReadFromJsonAsync<TestToolServerResponse>())!.Ok.Should().BeTrue();
            var tools = await Track(client.GetAsync(url + "/tools"));
            (await tools.Content.ReadFromJsonAsync<ToolItemView[]>())!.Should().NotBeEmpty();
            // A real MCP call, not only tools/list. Its result is untrusted tool data, not a public API response.
            using (var scope = factory.Services.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IToolConnectionRepository>();
                await using var connection = await scope.ServiceProvider.GetRequiredService<McpConnector>().ConnectAsync(owner,
                    (await repo.GetAsync(owner, headerId))!, await repo.GetCredentialAsync(owner, headerId));
                var result = await connection.Client.CallToolAsync("get_price", new Dictionary<string, object?> { ["symbol"] = "TEST" });
                result.IsError.Should().NotBeTrue();
            }
            var oauthCreated = await Track(client.PostAsJsonAsync("/v1/tools/servers", new CreateToolServerRequest("OAuth", auth.Resource)));
            var oauthId = (await oauthCreated.Content.ReadFromJsonAsync<ServerView>())!.Id;
            var start = await Track(client.PostAsJsonAsync($"/v1/tools/servers/{oauthId}/oauth/start", new StartToolOAuthRequest()), true);
            start.StatusCode.Should().Be(HttpStatusCode.OK);
            var authorizationUrl = (await start.Content.ReadFromJsonAsync<ToolOAuthStartResponse>())!.AuthorizationUrl!;
            var state = QueryHelpers.ParseQuery(new Uri(authorizationUrl).Query)["state"].ToString();
            (await start.Content.ReadAsStringAsync()).Should().Contain(state, "only the first OAuth start may carry this state");
            string verifier;
            using (var scope = factory.Services.CreateScope())
            {
                var states = scope.ServiceProvider.GetRequiredService<McpOAuthStateStore>();
                var record = await states.TakeAsync(state);
                record.Should().NotBeNull();
                verifier = record!.Verifier;
                await states.SaveAsync(state, record);
            }
            var sentinels = new[] { header, access, refresh, clientSecret, code, verifier, state };
            sentinels.Distinct(StringComparer.Ordinal).Should().HaveCount(7);
            using (var scope = factory.Services.CreateScope())
            {
                var stateFrame = (await scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>()
                    .GetDatabase().StringGetAsync($"mcp:oauth:{state}")).ToString();
                foreach (var sentinel in sentinels) stateFrame.Should().NotContain(sentinel);
            }
            using var browserHandler = new HttpClientHandler { AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.GetCertHashString() == auth.Certificate.GetCertHashString() };
            using var browser = new HttpClient(browserHandler);
            using var approval = await browser.GetAsync(authorizationUrl);
            approval.StatusCode.Should().Be(HttpStatusCode.Redirect);
            var query = QueryHelpers.ParseQuery(approval.Headers.Location!.Query);
            query["code"].ToString().Should().Be(code);
            query["state"].ToString().Should().Be(state);
            var completed = await Track(client.PostAsJsonAsync("/v1/tools/oauth/complete",
                new CompleteToolOAuthRequest(code, state, query["iss"].ToString())));
            completed.StatusCode.Should().Be(HttpStatusCode.OK);
            (await completed.Content.ReadFromJsonAsync<ServerView>())!.HasCredential.Should().BeTrue();
            using (var scope = factory.Services.CreateScope())
            {
                var credential = await scope.ServiceProvider.GetRequiredService<IToolConnectionRepository>()
                    .GetCredentialAsync(owner, oauthId);
                credential.Should().NotBeNull();
                var refreshed = await scope.ServiceProvider.GetRequiredService<McpOAuthService>().RefreshAsync(owner, oauthId);
                refreshed.AccessToken.Should().Be(access);
            }
            auth.RefreshCount.Should().BeGreaterThan(0);
            // Malicious upstream sends every sentinel in error text, response headers and body.
            var echo = string.Join("-", sentinels);
            mcp.MaliciousEcho = echo;
            mcp.EchoError = true;
            auth.ErrorDescription = echo;
            auth.EchoHeader = echo;
            auth.RejectRefresh = true;
            auth.RejectCode = true;
            var rejectedStart = await Track(client.PostAsJsonAsync($"/v1/tools/servers/{oauthId}/oauth/start", new StartToolOAuthRequest()), true);
            var rejectedUrl = (await rejectedStart.Content.ReadFromJsonAsync<ToolOAuthStartResponse>())!.AuthorizationUrl!;
            (await rejectedStart.Content.ReadAsStringAsync()).Should().NotContain(state);
            using (var rejectedApproval = await browser.GetAsync(rejectedUrl))
            {
                var rejectedQuery = QueryHelpers.ParseQuery(rejectedApproval.Headers.Location!.Query);
                var rejected = await Track(client.PostAsJsonAsync("/v1/tools/oauth/complete",
                    new CompleteToolOAuthRequest(code, rejectedQuery["state"].ToString(), rejectedQuery["iss"].ToString())));
                rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            }
            var failedTest = await Track(client.PostAsync(url + "/test", null));
            (await failedTest.Content.ReadFromJsonAsync<TestToolServerResponse>())!.Ok.Should().BeFalse();
            var failedList = await Track(client.GetAsync(url + "/tools"));
            failedList.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            using (var scope = factory.Services.CreateScope())
            {
                var oauth = scope.ServiceProvider.GetRequiredService<McpOAuthService>();
                Func<Task> refreshFailure = async () => { await oauth.RefreshAsync(owner, oauthId); };
                await refreshFailure.Should().ThrowAsync<McpOAuthException>();
                // Corrupt ciphertext with bytes unrelated to any sentinel: no decrypt error may reflect its content.
                var repo = scope.ServiceProvider.GetRequiredService<IToolConnectionRepository>();
                await repo.SaveCredentialAsync(owner, headerId, RandomNumberGenerator.GetBytes(48), "broken-key");
            }
            var malformed = await Track(client.PostAsync(url + "/test", null));
            (await malformed.Content.ReadFromJsonAsync<TestToolServerResponse>())!.Ok.Should().BeFalse();
            await Track(client.GetAsync("/v1/tools/servers"));
            await Track(client.GetAsync("/openapi/v1.json"));
            (await Track(client.DeleteAsync(url + "/credential"))).StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await Track(client.DeleteAsync($"/v1/tools/servers/{oauthId}/credential"))).StatusCode.Should().Be(HttpStatusCode.NoContent);

            // Exercise the bridge page-log frame channel with an owner-scoped missing presentation.
            var ticket = Guid.NewGuid().ToString("N");
            await factory.Services.GetRequiredService<ITicketStore>().IssueAsync(ticket, owner);
            using (var socket = await factory.Server.CreateWebSocketClient()
                .ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None))
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "auth", ticket })),
                    WebSocketMessageType.Text, true, CancellationToken.None);
                var pageFrames = new List<string>();
                pageFrames.Add(await ReadFrameAsync(socket));
                await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"start\",\"presentation\":\"missing-hygiene\"}"),
                    WebSocketMessageType.Text, true, CancellationToken.None);
                for (var i = 0; i < 8 && !pageFrames.Any(frame => frame.Contains("\"type\":\"log\"", StringComparison.Ordinal)); i++)
                    pageFrames.Add(await ReadFrameAsync(socket));
                pageFrames.Should().Contain(frame => frame.Contains("\"type\":\"log\"", StringComparison.Ordinal));
                foreach (var frame in pageFrames)
                    foreach (var sentinel in sentinels) frame.Should().NotContain(sentinel);
            }

            foreach (var (response, isStart) in responses)
            {
                var body = await response.Content.ReadAsStringAsync();
                var headers = response.Headers + " " + response.Content.Headers;
                foreach (var sentinel in sentinels)
                {
                    if (!isStart || sentinel != state) body.Should().NotContain(sentinel);
                    headers.Should().NotContain(sentinel);
                    response.ReasonPhrase.Should().NotContain(sentinel);
                }
            }
            foreach (var line in logger.Messages)
                foreach (var sentinel in sentinels)
                    line.Should().NotContain(sentinel);
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PresenterAiDbContext>();
                var servers = await db.ToolServers.AsNoTracking().Where(s => s.OwnerId == owner).ToListAsync();
                var credentials = await db.ToolServerCredentials.AsNoTracking()
                    .Where(c => servers.Select(s => s.Id).Contains(c.ServerId)).ToListAsync();
                var stored = JsonSerializer.Serialize(servers) + JsonSerializer.Serialize(credentials);
                foreach (var sentinel in sentinels) stored.Should().NotContain(sentinel);
                var redisDb = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
                (await redisDb.KeyExistsAsync($"mcp:oauth:{state}")).Should().BeFalse();
            }
        }
        finally
        {
            foreach (var (response, _) in responses) response.Dispose();
        }
    }

    private static async Task<string> ReadFrameAsync(WebSocket socket)
    {
        var buffer = new byte[32768];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("Bridge closed before page log.");
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class CaptureProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Messages);
        public void Dispose() { }

        private sealed class CaptureLogger(string category, ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => EmptyScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Enqueue(category + ": " + formatter(state, exception) + exception?.ToString());
            private sealed class EmptyScope : IDisposable
            {
                public static readonly EmptyScope Instance = new();
                public void Dispose() { }
            }
        }
    }
}
