using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PresenterAi.Application.Auth;
using PresenterAi.Application.Content;
using PresenterAi.Application.Presenting;
using PresenterAi.Infrastructure.Content;
using Microsoft.IdentityModel.Tokens;

namespace PresenterAi.Api.Tests.Infrastructure;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public IReadOnlyDictionary<string, string?>? Overrides { get; set; }
    public string EnvironmentName { get; set; } = "Testing";

    public HttpClient CreateAuthenticatedClient(string? userId = null, string? email = null, string role = "user")
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", CreateTestToken(userId ?? "test-user", email ?? "test@presenter-ai.local", role));
        return client;
    }

    public static string CreateTestToken(string userId, string email, string role = "user")
    {
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: "https://test.presenter-ai.local",
            audience: "presenter-ai-tests",
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId),
                new Claim(JwtRegisteredClaimNames.Email, email),
                new Claim("role", role),
                new Claim(ClaimTypes.Role, role)
            ],
            notBefore: now,
            expires: now.AddMinutes(10),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-only-jwt-secret-key-not-a-credential-123456")),
                SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        builder.UseSetting("Upstream:Endpoint", "https://api.openai.com");
        builder.UseSetting("Upstream:Key", "test");
        builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Port=9;Database=presenter_ai_test;Username=test;Password=test");
        builder.UseSetting("ConnectionStrings:Redis", "localhost:9");
        builder.UseSetting("Jwt:Issuer", "https://test.presenter-ai.local");
        builder.UseSetting("Jwt:Audience", "presenter-ai-tests");
        builder.UseSetting("Jwt:SecretKey", "test-only-jwt-secret-key-not-a-credential-123456");
        builder.UseSetting("Jwt:AccessTokenMinutes", "60");
        builder.UseSetting("Jwt:RefreshTokenDays", "30");
        builder.UseSetting("RateLimiting:Enabled", "false");
        builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:47914");
        builder.UseSetting("Cors:AllowedOrigins:1", "http://localhost:47915");
        builder.UseSetting("Auth:Dev:Enabled", "true");
        builder.UseSetting("Auth:Dev:UserId", "test-user");
        builder.UseSetting("Auth:Dev:Email", "test@presenter-ai.local");
        builder.UseSetting("Auth:Dev:DisplayName", "Test User");
        builder.UseSetting("Content:RootDir", FindRepositoryRoot());
        // No web root by default: a test that wants the SPA served must opt in with WebRootFixture, so nothing
        // passes only because web/app/dist happens to be built on the developer's machine (CI never builds it).
        builder.UseSetting("Content:WebRoot", Path.Combine(Path.GetTempPath(), "presenter-ai-no-web-root"));

        if (Overrides is not null)
        {
            foreach (var setting in Overrides)
            {
                builder.UseSetting(setting.Key, setting.Value);
            }
        }

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITicketStore>();
            services.RemoveAll<IPresentationRepository>();
            services.AddScoped<IPresentationRepository, TestPresentationRepository>();
            services.AddSingleton<TestTicketStore>();
            services.AddSingleton<ITicketStore>(serviceProvider => serviceProvider.GetRequiredService<TestTicketStore>());
        });
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PresenterAi.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("PresenterAi.slnx was not found above the test assembly.");
    }
}

internal sealed class TestPresentationRepository : IPresentationRepository
{
    private readonly FilePresentationRepository _source;

    public TestPresentationRepository(IOptions<ContentOptions> options, IWebHostEnvironment environment)
    {
        _source = new FilePresentationRepository(Path.GetFullPath(options.Value.RootDir, environment.ContentRootPath));
    }

    public async Task<PresentationListResult> ListAsync(
        string ownerId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(ownerId);
        var rows = await _source.ListFilesAsync(cancellationToken).ConfigureAwait(false);
        var offset = (long)(page - 1) * pageSize;
        var skip = (int)Math.Min(offset, int.MaxValue);
        return new PresentationListResult(rows.Skip(skip).Take(pageSize).ToArray(), rows.Count);
    }

    public Task<LoadedPresentation> LoadAsync(string ownerId, string id, CancellationToken cancellationToken = default)
    {
        EnsureOwner(ownerId);
        return _source.ReadAsync(id, cancellationToken);
    }

    private static void EnsureOwner(string ownerId)
    {
        if (!string.Equals(ownerId, "test-user", StringComparison.Ordinal))
        {
            throw new FileNotFoundException("The owner-scoped presentation was not found.");
        }
    }
}

internal sealed class TestTicketStore : ITicketStore
{
    private readonly ConcurrentDictionary<string, (string UserId, DateTimeOffset ExpiresAt)> _tickets = new();

    public Task IssueAsync(string ticketId, string userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tickets[ticketId] = (userId, DateTimeOffset.UtcNow.AddMinutes(1));
        return Task.CompletedTask;
    }

    public Task<string?> ClaimAsync(string ticketId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_tickets.TryRemove(ticketId, out var ticket) || ticket.ExpiresAt <= DateTimeOffset.UtcNow)
            return Task.FromResult<string?>(null);
        return Task.FromResult<string?>(ticket.UserId);
    }

    public void Expire(string ticketId)
    {
        if (_tickets.TryGetValue(ticketId, out var ticket))
            _tickets[ticketId] = (ticket.UserId, DateTimeOffset.UtcNow.AddSeconds(-1));
    }
}
