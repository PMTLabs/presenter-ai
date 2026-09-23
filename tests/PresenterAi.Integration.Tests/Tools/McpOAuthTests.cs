using System.Security.Cryptography;
using System.Net;
using System.Net.Security;
using Microsoft.Extensions.Logging;
using PresenterAi.Infrastructure.Tools;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using PresenterAi.Application.Tools.External;
using PresenterAi.Infrastructure.Persistence;
using PresenterAi.Infrastructure.Persistence.Entities;
using PresenterAi.Infrastructure.Tools.Mcp;
using PresenterAi.Infrastructure.Tests.Tools;
using PresenterAi.Integration.Tests.Support;
using StackExchange.Redis;
using Xunit;

namespace PresenterAi.Integration.Tests.Tools;

[Collection(IntegrationCollection.Name)]
public sealed class McpOAuthTests(RedisFixture redis, PostgresFixture postgres)
{
    [Fact]
    public async Task State_is_encrypted_one_use_and_bound_to_its_owner()
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var protector = new CredentialProtector(Options.Create(new ExternalToolsOptions
        {
            CredentialKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        }));
        var store = new McpOAuthStateStore(connection, protector);
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var value = new McpOAuthState("owner", Guid.NewGuid(), "verifier-sentinel", "https://issuer.example",
            "https://issuer.example/token", "client", "secret-sentinel", "none", "https://resource.example/mcp",
            "https://app.example/tools/oauth/callback", "pre-registered", false);
        await store.SaveAsync(state, value);
        var raw = await connection.GetDatabase().StringGetAsync($"mcp:oauth:{state}");
        Assert.DoesNotContain("secret-sentinel", raw.ToString());
        Assert.DoesNotContain("verifier-sentinel", raw.ToString());
        var taken = await store.TakeAsync(state);
        Assert.Equal(value, taken);
        Assert.Null(await store.TakeAsync(state));
    }

    [Fact]
    public async Task Another_owner_consumes_state_before_code_exchange()
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var protector = new CredentialProtector(Options.Create(new ExternalToolsOptions
        {
            CredentialKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        }));
        var store = new McpOAuthStateStore(connection, protector);
        await using var db = new PresenterAiDbContext(new DbContextOptionsBuilder<PresenterAiDbContext>()
            .UseNpgsql(postgres.ConnectionString).Options);
        await db.Database.MigrateAsync();
        var user = new User { Email = $"{Guid.NewGuid():N}@example.test", AuthMethod = "test",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var repo = new PostgresToolConnectionRepository(db);
        var server = await repo.AddAsync(user.Id, "OAuth", "https://resource.example/mcp");
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await store.SaveAsync(state, new McpOAuthState(user.Id, server.Id, "verifier", "https://issuer.example",
            "https://issuer.example/token", "client", null, "none", server.Url,
            "https://app.example/tools/oauth/callback", "pre-registered", false));
        var services = new ServiceCollection();
        services.AddDbContext<PresenterAiDbContext>(options => options.UseNpgsql(postgres.ConnectionString));
        services.AddScoped<IToolConnectionRepository, PostgresToolConnectionRepository>();
        using var provider = services.BuildServiceProvider();
        var service = new McpOAuthService(new UnusedClientFactory(), new StrictOutboundAddressPolicy(), provider.GetRequiredService<IServiceScopeFactory>(),
            store, protector, connection, Options.Create(new ExternalToolsOptions()),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OAuth:ApiBaseUrl"] = "https://app.example"
            }).Build());
        var failure = await Assert.ThrowsAsync<McpOAuthException>(() => service.CompleteAsync("different-owner", "code", state));
        Assert.Equal("tools_oauth_state_invalid", failure.Code);
        Assert.Null(await store.TakeAsync(state));
        Assert.Null(await repo.GetCredentialAsync(user.Id, server.Id));
    }

    [Fact]
    public async Task Wrong_issuer_is_rejected_and_state_is_consumed()
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var protector = new CredentialProtector(Options.Create(new ExternalToolsOptions
        {
            CredentialKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        }));
        var store = new McpOAuthStateStore(connection, protector);
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await store.SaveAsync(state, new McpOAuthState("owner", Guid.NewGuid(), "verifier", "https://issuer.example",
            "https://issuer.example/token", "client", null, "none", "https://resource.example/mcp",
            "https://app.example/tools/oauth/callback", "pre-registered", true));
        using var provider = new ServiceCollection().BuildServiceProvider();
        var service = new McpOAuthService(new UnusedClientFactory(), new StrictOutboundAddressPolicy(),
            provider.GetRequiredService<IServiceScopeFactory>(), store, protector, connection,
            Options.Create(new ExternalToolsOptions()), new ConfigurationBuilder().Build());
        var error = await Assert.ThrowsAsync<McpOAuthException>(() =>
            service.CompleteAsync("owner", "code", state, "https://other.example"));
        Assert.Equal("tools_oauth_failed", error.Code);
        Assert.Null(await store.TakeAsync(state));
    }

    [Theory]
    [InlineData("pre-registered")]
    [InlineData("metadata document")]
    [InlineData("dynamic")]
    public async Task Registration_paths_exchange_strict_pkce_and_store_encrypted_tokens(string registration)
    {
        await using var fake = await StrictFakeAuthServer.StartAsync();
        if (registration == "dynamic") fake.ClientMetadata = false;
        await using var harness = await Harness.CreateAsync(fake, postgres, redis);
        var clientId = registration == "pre-registered" ? "registered-client" : null;
        var secret = registration == "pre-registered" ? "registered-secret" : null;
        var (credential, state) = await harness.ConnectAsync(clientId: clientId, clientSecret: secret);
        Assert.Equal(registration, credential.Registration);
        Assert.Equal(fake.Resource, credential.Resource);
        Assert.Single(fake.Challenges);
        Assert.Single(fake.CodeChallenges);
        Assert.Single(fake.TokenGrants);
        Assert.Equal("authorization_code", fake.TokenGrants.Single());
        Assert.Equal(fake.Resource, fake.TokenResources.Single());
        Assert.Null(await harness.StateStore.TakeAsync(state));
        var stored = (await harness.Repository.GetCredentialAsync(harness.Owner, harness.ServerId))!;
        Assert.DoesNotContain(credential.AccessToken, Convert.ToBase64String(stored.Ciphertext));
        Assert.DoesNotContain(credential.RefreshToken!, Convert.ToBase64String(stored.Ciphertext));
        Assert.Equal(credential, JsonSerializer.Deserialize<OAuthTokenCredential>(harness.Protector.Unprotect(
            harness.Owner, harness.ServerId, stored).Payload!));
        Assert.Equal(registration == "dynamic" ? 1 : 0, fake.RegistrationCount);
        Assert.Equal(registration == "metadata document" ? 1 : 0, fake.MetadataFetchCount);
    }

    [Fact]
    public async Task Registration_priority_and_client_required_fallback()
    {
        await using var fake = await StrictFakeAuthServer.StartAsync();
        await using var harness = await Harness.CreateAsync(fake, postgres, redis);
        await harness.ConnectAsync(clientId: "registered-client", clientSecret: "registered-secret");
        Assert.Equal(0, fake.MetadataFetchCount);
        Assert.Equal(0, fake.RegistrationCount);
        await harness.ConnectAsync();
        Assert.Equal(1, fake.MetadataFetchCount);
        Assert.Equal(0, fake.RegistrationCount);
        fake.ClientMetadata = false;
        fake.Registration = false;
        var error = await Assert.ThrowsAsync<McpOAuthException>(() => harness.Service.StartAsync(harness.Owner, harness.ServerId));
        Assert.Equal("tools_oauth_client_required", error.Code);
    }

    [Fact]
    public async Task Http_api_base_skips_metadata_document_and_uses_dynamic_registration()
    {
        await using var fake = await StrictFakeAuthServer.StartAsync();
        await using var harness = await Harness.CreateAsync(fake, postgres, redis, "http://localhost:47913");
        var (credential, _) = await harness.ConnectAsync();
        Assert.Equal("dynamic", credential.Registration);
        Assert.Equal(0, fake.MetadataFetchCount);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("client_secret_post")]
    [InlineData("client_secret_basic")]
    public async Task Dynamic_registration_token_auth_method_is_used_at_exchange_and_refresh(string method)
    {
        await using var fake = await StrictFakeAuthServer.StartAsync();
        fake.ClientMetadata = false;
        fake.RegisteredAuthMethod = method;
        await using var harness = await Harness.CreateAsync(fake, postgres, redis);
        var (credential, _) = await harness.ConnectAsync();
        Assert.Equal(method, credential.TokenAuthMethod);
        var refreshed = await harness.Service.RefreshAsync(harness.Owner, harness.ServerId);
        Assert.NotEqual(credential.RefreshToken, refreshed.RefreshToken);
        Assert.Equal(1, fake.RefreshCount);
        Assert.Equal(refreshed, harness.ReadStored());
    }

    [Fact]
    public async Task Incompatible_registration_is_rejected()
    {
        await using var fake = await StrictFakeAuthServer.StartAsync();
        fake.ClientMetadata = false;
        fake.IncompatibleRegistration = true;
        await using var harness = await Harness.CreateAsync(fake, postgres, redis);
        var error = await Assert.ThrowsAsync<McpOAuthException>(() => harness.Service.StartAsync(harness.Owner, harness.ServerId));
        Assert.Equal("tools_oauth_unsupported", error.Code);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Discovery_challenge_path_root_and_oidc_fallback(bool noChallenge, bool noPath, bool oidc)
    {
        await using var fake = await StrictFakeAuthServer.StartAsync();
        fake.PathMetadata = !noPath;
        fake.OidcOnly = oidc;
        await using var harness = await Harness.CreateAsync(fake, postgres, redis);
        var start = await harness.Service.StartAsync(harness.Owner, harness.ServerId,
            noChallenge ? null : fake.ChallengeHeader, "registered-client", "registered-secret");
        Assert.StartsWith(fake.Url + "/authorize?", start.AuthorizationUrl);
        if (noPath) Assert.Contains("/.well-known/oauth-protected-resource", fake.DiscoveryRequests);
        if (oidc) Assert.Contains("/.well-known/openid-configuration", fake.DiscoveryRequests);
    }

    [Fact]
    public async Task No_s256_or_mismatched_resource_is_rejected()
    {
        await using var fake = await StrictFakeAuthServer.StartAsync();
        await using var harness = await Harness.CreateAsync(fake, postgres, redis);
        fake.WrongResource = true;
        var mismatch = await Assert.ThrowsAsync<McpOAuthException>(() => harness.Service.StartAsync(harness.Owner, harness.ServerId));
        Assert.Equal("tools_oauth_unsupported", mismatch.Code);
        fake.WrongResource = false;
        fake.S256 = false;
        var plain = await Assert.ThrowsAsync<McpOAuthException>(() => harness.Service.StartAsync(harness.Owner, harness.ServerId));
        Assert.Equal("tools_oauth_unsupported", plain.Code);
    }

    [Fact]
    public async Task Blocked_challenge_metadata_url_is_refused_without_contacting_the_fallback()
    {
        await using var fake = await StrictFakeAuthServer.StartAsync();
        fake.BlockedMetadata = true;
        await using var harness = await Harness.CreateAsync(fake, postgres, redis);
        var error = await Assert.ThrowsAsync<McpOAuthException>(() => harness.Service.StartAsync(harness.Owner, harness.ServerId,
            fake.ChallengeHeader, "registered-client", "registered-secret"));
        Assert.Equal("tools_url_blocked", error.Code);
        Assert.Empty(fake.DiscoveryRequests);
    }

    [Fact]
    public async Task Reused_state_and_wrong_issuer_fail_without_exchanging_tokens()
    {
        await using var fake = await StrictFakeAuthServer.StartAsync();
        await using var harness = await Harness.CreateAsync(fake, postgres, redis);
        var (start, code, state, iss) = await harness.BeginAsync();
        Assert.NotEmpty(start.AuthorizationUrl);
        await Assert.ThrowsAsync<McpOAuthException>(() => harness.Service.CompleteAsync(harness.Owner, code, state,
            "https://other.example"));
        var reused = await Assert.ThrowsAsync<McpOAuthException>(() => harness.Service.CompleteAsync(harness.Owner, code, state, iss));
        Assert.Equal("tools_oauth_state_invalid", reused.Code);
        Assert.Equal(0, fake.TokenCount);
    }

    [Fact]
    public async Task Concurrent_refreshes_across_one_instance_exchange_once()
    {
        await using var fake = await StrictFakeAuthServer.StartAsync();
        fake.RefreshDelayMs = 400;
        await using var harness = await Harness.CreateAsync(fake, postgres, redis);
        await harness.ConnectAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            harness.Service.RefreshAsync(harness.Owner, harness.ServerId)));
        Assert.Equal(1, fake.RefreshCount);
        Assert.Equal(results[0].AccessToken, results[1].AccessToken);
        Assert.Equal("connected", (await harness.Repository.GetAsync(harness.Owner, harness.ServerId))!.Status);
    }

    [Fact]
    public async Task Two_instances_share_lease_and_winner_token_even_when_second_saw_old_credential()
    {
        await using var fake = await StrictFakeAuthServer.StartAsync();
        fake.RefreshDelayMs = 400;
        await using var harness = await Harness.CreateAsync(fake, postgres, redis);
        await harness.ConnectAsync();
        // Separate service instances and DbContext scopes, sharing the same Redis and persisted ciphertext.
        var second = harness.Provider.GetRequiredService<McpOAuthService>();
        var results = await Task.WhenAll(harness.Service.RefreshAsync(harness.Owner, harness.ServerId),
            second.RefreshAsync(harness.Owner, harness.ServerId));
        Assert.Equal(1, fake.RefreshCount);
        Assert.Equal(results[0].RefreshToken, results[1].RefreshToken);
        Assert.Equal("connected", (await harness.Repository.GetAsync(harness.Owner, harness.ServerId))!.Status);
    }

    [Fact]
    public async Task Refresh_invalid_grant_with_unchanged_version_marks_reconnect()
    {
        await using var fake = await StrictFakeAuthServer.StartAsync();
        await using var harness = await Harness.CreateAsync(fake, postgres, redis);
        await harness.ConnectAsync();
        fake.RejectRefresh = true;
        var error = await Assert.ThrowsAsync<McpOAuthException>(() =>
            harness.Service.RefreshAsync(harness.Owner, harness.ServerId));
        Assert.Equal("tools_oauth_invalid_grant", error.Code);
        var server = await harness.Repository.GetAsync(harness.Owner, harness.ServerId);
        Assert.Equal("needs_reconnect", server!.Status);
        Assert.Equal("oauth_invalid_grant", server.LastErrorCode);
    }

    [Fact]
    public async Task Cross_instance_invalid_grant_reloads_winners_token_instead_of_marking_reconnect()
    {
        await using var fake = await StrictFakeAuthServer.StartAsync();
        fake.RefreshDelayMs = 350;
        fake.SecondRefreshDelayMs = 800;
        await using var harness = await Harness.CreateAsync(fake, postgres, redis);
        await harness.ConnectAsync();
        var first = harness.Service.RefreshAsync(harness.Owner, harness.ServerId);
        using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        // Simulate a lease that expires while the first instance's token request is in flight.
        await SpinWaitUntilAsync(() => Volatile.Read(ref fake.RefreshCount) >= 1);
        await connection.GetDatabase().KeyDeleteAsync($"mcp:refresh:{harness.ServerId}");
        var second = harness.Provider.GetRequiredService<McpOAuthService>()
            .RefreshAsync(harness.Owner, harness.ServerId);
        var winner = await first;
        var loser = await second;
        Assert.Equal(2, fake.RefreshCount);
        Assert.Equal(winner.RefreshToken, loser.RefreshToken);
        Assert.Equal(winner, harness.ReadStored());
        Assert.Equal("connected", (await harness.Repository.GetAsync(harness.Owner, harness.ServerId))!.Status);
    }

    private static async Task SpinWaitUntilAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class LoopbackPolicy : IOutboundAddressPolicy
    {
        public bool IsAllowed(IPAddress address) => IPAddress.IsLoopback(address);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required ServiceProvider Provider { get; init; }
        public required StrictFakeAuthServer Fake { get; init; }
        public required string Owner { get; init; }
        public required Guid ServerId { get; init; }
        public McpOAuthService Service => Provider.GetRequiredService<McpOAuthService>();
        public McpOAuthStateStore StateStore => Provider.GetRequiredService<McpOAuthStateStore>();
        public CredentialProtector Protector => Provider.GetRequiredService<CredentialProtector>();
        public IToolConnectionRepository Repository => Provider.GetRequiredService<IToolConnectionRepository>();
        public OAuthTokenCredential ReadStored()
        {
            var row = Repository.GetCredentialAsync(Owner, ServerId).GetAwaiter().GetResult()!;
            return JsonSerializer.Deserialize<OAuthTokenCredential>(Protector.Unprotect(Owner, ServerId, row).Payload!)!;
        }
        public static async Task<Harness> CreateAsync(StrictFakeAuthServer fake, PostgresFixture postgres,
            RedisFixture redis, string? baseUrl = null)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OAuth:ApiBaseUrl"] = baseUrl ?? fake.Url,
                ["Tools:OAuthRedirectUri"] = fake.RedirectUri,
                ["Tools:CredentialKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.ClearProviders());
            services.AddSingleton<IConfiguration>(configuration);
            services.AddOptions<ExternalToolsOptions>().Bind(configuration.GetSection("Tools"));
            services.AddSingleton<CredentialProtector>();
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis.ConnectionString));
            services.AddDbContext<PresenterAiDbContext>(options => options.UseNpgsql(postgres.ConnectionString));
            services.AddScoped<IToolConnectionRepository, PostgresToolConnectionRepository>();
            services.AddSingleton<IOutboundAddressPolicy, LoopbackPolicy>();
            services.AddExternalTools();
            var provider = services.BuildServiceProvider();
            provider.GetRequiredService<SocketsHttpHandler>().SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert?.GetCertHashString() == fake.Certificate.GetCertHashString()
            };
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PresenterAiDbContext>();
            await db.Database.MigrateAsync();
            var user = new User { Email = $"{Guid.NewGuid():N}@example.test", AuthMethod = "test",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            var repo = scope.ServiceProvider.GetRequiredService<IToolConnectionRepository>();
            var server = await repo.AddAsync(user.Id, "Strict OAuth", fake.Resource);
            return new Harness { Provider = provider, Owner = user.Id, ServerId = server.Id, Fake = fake };
        }
        public async Task<(OAuthTokenCredential Credential, string State)> ConnectAsync(string? clientId = null,
            string? clientSecret = null)
        {
            var (_, code, state, iss) = await BeginAsync(clientId, clientSecret);
            await Service.CompleteAsync(Owner, code, state, iss);
            return (ReadStored(), state);
        }
        public async Task<(OAuthStartResult Start, string Code, string State, string Iss)> BeginAsync(
            string? clientId = null, string? clientSecret = null)
        {
            var challenge = await Service.ProbeChallengeAsync(Fake.Resource);
            Assert.Equal(Fake.ChallengeHeader, challenge);
            var start = await Service.StartAsync(Owner, ServerId, challenge, clientId, clientSecret);
            using var handler = new HttpClientHandler { AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                    cert?.GetCertHashString() == Fake.Certificate.GetCertHashString() };
            using var client = new HttpClient(handler);
            using var response = await client.GetAsync(start.AuthorizationUrl);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(response.Headers.Location!.Query);
            return (start, query["code"].ToString(), query["state"].ToString(), query["iss"].ToString());
        }
        public async ValueTask DisposeAsync() => await Provider.DisposeAsync();
    }

    private sealed class UnusedClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Unexpected outbound request");
    }
}
