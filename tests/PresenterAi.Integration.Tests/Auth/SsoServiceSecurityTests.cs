using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PresenterAi.Api.Auth;
using PresenterAi.Infrastructure.Identity;
using PresenterAi.Infrastructure.Persistence;
using PresenterAi.Infrastructure.Persistence.Entities;
using PresenterAi.Application.Auth;
using PresenterAi.Integration.Tests.Support;
using Xunit;

namespace PresenterAi.Integration.Tests.Auth;

[Collection(IntegrationCollection.Name)]
public sealed class SsoServiceSecurityTests : IAsyncLifetime
{
    private const string Verifier = "3xY7pQfL2mNvR8sT1uW4zA6bC9dE0gH5jK7lM2nP4qS";
    private static readonly string Challenge = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(Verifier)))
        .Replace("+", "-").Replace("/", "_").TrimEnd('=');
    private static readonly string StateKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private readonly PresenterAiDbContext _db;
    private readonly FakeCodeStore _codes = new();
    private readonly FakeStateStore _states = new();
    private readonly SsoService _service;

    public SsoServiceSecurityTests(PostgresFixture postgres)
    {
        _db = new PresenterAiDbContext(new DbContextOptionsBuilder<PresenterAiDbContext>()
            .UseNpgsql(postgres.ConnectionString).Options);
        _service = CreateService();
    }

    public async Task InitializeAsync()
    {
        await _db.Database.MigrateAsync();
        await _db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE session_turns, sessions, presentations, refresh_tokens, external_logins, users CASCADE");
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Provider_subject_linking_is_idempotent()
    {
        var first = await _service.ResolveIdentityAsync(
            new SsoService.ProviderIdentity("google", "subject-1", "one@example.test", "One", true));
        var second = await _service.ResolveIdentityAsync(
            new SsoService.ProviderIdentity("google", "subject-1", "changed@example.test", "Changed", false));

        second.Id.Should().Be(first.Id);
        (await _db.Users.CountAsync()).Should().Be(1);
        (await _db.ExternalLogins.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Verified_email_links_to_existing_user_and_lookup_is_case_insensitive()
    {
        var existing = new User { Email = "owner@example.test", AuthMethod = "dev" };
        _db.Users.Add(existing);
        await _db.SaveChangesAsync();

        var linked = await _service.ResolveIdentityAsync(
            new SsoService.ProviderIdentity("microsoft", "subject-2", "OWNER@EXAMPLE.TEST", "Owner", true));

        linked.Id.Should().Be(existing.Id);
        (await _db.ExternalLogins.SingleAsync()).ProviderEmailVerified.Should().BeTrue();
    }

    [Fact]
    public async Task Unverified_email_collision_is_refused_without_touching_existing_account()
    {
        var existing = new User { Email = "owner@example.test", DisplayName = "Original", AuthMethod = "dev" };
        _db.Users.Add(existing);
        await _db.SaveChangesAsync();

        var act = () => _service.ResolveIdentityAsync(
            new SsoService.ProviderIdentity("google", "attacker", "OWNER@EXAMPLE.TEST", "Attacker", false));

        await act.Should().ThrowAsync<SsoFailureException>()
            .Where(exception => exception.Code == "auth.signup_not_allowed");
        (await _db.Users.CountAsync()).Should().Be(1);
        (await _db.ExternalLogins.CountAsync()).Should().Be(0);
        (await _db.Users.SingleAsync()).DisplayName.Should().Be("Original");
    }

    [Fact]
    public async Task Sign_in_allowlist_is_checked_before_user_insert()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["Auth:SignIn:AllowedEmailDomains:0"] = "allowed.test"
        });

        var act = () => service.ResolveIdentityAsync(
            new SsoService.ProviderIdentity("google", "outside", "outside@blocked.test", "Outside", true));

        await act.Should().ThrowAsync<SsoFailureException>()
            .Where(exception => exception.Code == "auth.signup_not_allowed");
        (await _db.Users.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Bootstrap_email_is_admin_on_first_sign_in()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["Admin:BootstrapEmails:0"] = "ADMIN@EXAMPLE.TEST"
        });

        var user = await service.ResolveIdentityAsync(
            new SsoService.ProviderIdentity("google", "admin-subject", "admin@example.test", "Admin", true));

        user.Role.Should().Be("admin");
    }

    [Fact]
    public async Task Nonce_replay_is_rejected()
    {
        var state = SealState("google", DateTimeOffset.UtcNow, "nonce-replay");
        var first = () => _service.HandleCallbackAsync("google", "provider-code", state);
        await first();

        var second = () => _service.HandleCallbackAsync("google", "provider-code", state);
        await second.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already been used*");
    }

    [Fact]
    public async Task Pkce_requires_the_correct_non_truncated_verifier()
    {
        var user = new User { Email = "pkce@example.test", AuthMethod = "google" };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        foreach (var verifier in new string?[] { null, "wrong-verifier-that-is-long-enough-xxxxxxxxxxxxxxxxxxxxxxxx", "short" })
        {
            var code = Guid.NewGuid().ToString("N");
            _codes.Set(code, new SsoCode(user.Id, Challenge, "https://app.example.test/callback"));
            var act = () => _service.RedeemCodeAsync(code, verifier);
            await act.Should().ThrowAsync<SsoFailureException>()
                .Where(exception => exception.Code == "auth.sso_state_invalid");
        }

        var validCode = Guid.NewGuid().ToString("N");
        _codes.Set(validCode, new SsoCode(user.Id, Challenge, "https://app.example.test/callback"));
        (await _service.RedeemCodeAsync(validCode, Verifier)).Id.Should().Be(user.Id);
    }

    [Fact]
    public async Task Sso_codes_are_one_time_and_expired_codes_are_rejected()
    {
        var user = new User { Email = "code@example.test", AuthMethod = "google" };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var code = Guid.NewGuid().ToString("N");
        _codes.Set(code, new SsoCode(user.Id, Challenge, "https://app.example.test/callback"));
        (await _service.RedeemCodeAsync(code, Verifier)).Id.Should().Be(user.Id);
        var replay = () => _service.RedeemCodeAsync(code, Verifier);
        await replay.Should().ThrowAsync<SsoFailureException>()
            .Where(exception => exception.Code == "auth.sso_code_used");

        var expired = () => _service.RedeemCodeAsync("expired", Verifier);
        await expired.Should().ThrowAsync<SsoFailureException>()
            .Where(exception => exception.Code == "auth.sso_code_used");
    }

    [Fact]
    public async Task Disabled_account_is_refused_on_callback()
    {
        var user = new User { Email = "disabled@example.test", AuthMethod = "google", IsDisabled = true };
        _db.Users.Add(user);
        _db.ExternalLogins.Add(new ExternalLogin
        {
            User = user, UserId = user.Id, Provider = "google", Subject = "disabled-subject",
            ProviderEmail = user.Email, ProviderEmailVerified = true
        });
        await _db.SaveChangesAsync();

        var service = CreateService(httpClientFactory: new FakeHttpClientFactory("disabled-subject", user.Email));
        var act = () => service.HandleCallbackAsync("google", "provider-code", SealState("google", DateTimeOffset.UtcNow, "disabled-nonce"));
        await act.Should().ThrowAsync<SsoFailureException>()
            .Where(exception => exception.Code == "auth.account_disabled");
    }

    private SsoService CreateService(
        IReadOnlyDictionary<string, string?>? extra = null,
        IHttpClientFactory? httpClientFactory = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(extra ?? new Dictionary<string, string?>()).Build();
        var options = Options.Create(new OAuthOptions
        {
            ApiBaseUrl = "https://api.example.test",
            StateEncryptionKey = StateKey,
            AllowedRedirectUris = ["https://app.example.test/callback"],
            Google = new OAuthProviderOptions
            {
                Enabled = true, ClientId = "google", ClientSecret = "not-a-secret",
                AuthorizationEndpoint = "https://provider.test/authorize",
                TokenEndpoint = "https://provider.test/token",
                UserInfoEndpoint = "https://provider.test/userinfo"
            }
        });
        return new SsoService(
            _db, options, httpClientFactory ?? new FakeHttpClientFactory(), new Lazy<ISsoCodeStore>(() => _codes),
            new Lazy<ISsoStateStore>(() => _states), new SignInPolicy(configuration),
            TimeProvider.System, NullLogger<SsoService>.Instance);
    }

    private static string SealState(string provider, DateTimeOffset timestamp, string nonce)
    {
        var json = JsonSerializer.Serialize(new
        {
            state = "client-state",
            clientHint = "web",
            redirectUri = "https://app.example.test/callback",
            provider,
            codeChallenge = Challenge,
            nonce,
            timestamp
        });
        var clear = Encoding.UTF8.GetBytes(json);
        var iv = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[clear.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Convert.FromBase64String(StateKey), 16);
        aes.Encrypt(iv, clear, cipher, tag);
        return Convert.ToBase64String([.. iv, .. tag, .. cipher]);
    }

    private sealed class FakeCodeStore : ISsoCodeStore
    {
        private readonly Dictionary<string, SsoCode> _values = new();
        public void Set(string rawCode, SsoCode code) => _values[TokenService.Hash(rawCode)] = code;
        public Task IssueAsync(string codeHash, SsoCode code, CancellationToken cancellationToken = default)
        {
            _values[codeHash] = code;
            return Task.CompletedTask;
        }
        public Task<SsoCode?> ClaimAsync(string codeHash, CancellationToken cancellationToken = default)
        {
            _values.Remove(codeHash, out var value);
            return Task.FromResult<SsoCode?>(value);
        }
    }

    private sealed class FakeStateStore : ISsoStateStore
    {
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);
        public Task<bool> TryConsumeNonceAsync(string nonce, CancellationToken cancellationToken = default) =>
            Task.FromResult(_used.Add(nonce));
    }

    private sealed class FakeHttpClientFactory(string subject = "callback-subject", string email = "callback@example.test") : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ProviderHandler(subject, email), disposeHandler: false);
    }

    private sealed class ProviderHandler(string subject, string email) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    request.Method == HttpMethod.Post
                        ? "{\"access_token\":\"provider-access-token\"}"
                        : $"{{\"sub\":\"{subject}\",\"email\":\"{email}\",\"email_verified\":true,\"name\":\"Callback\"}}", 
                    Encoding.UTF8,
                    "application/json")
            });
    }
}
