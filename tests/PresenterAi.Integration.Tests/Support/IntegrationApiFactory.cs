using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace PresenterAi.Integration.Tests.Support;

public sealed class IntegrationApiFactory(PostgresFixture postgres, RedisFixture redis) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString);
        builder.UseSetting("ConnectionStrings:Redis", redis.ConnectionString);
        builder.UseSetting("Upstream:Endpoint", "https://api.openai.com");
        builder.UseSetting("Upstream:Key", "integration-test-only");
        builder.UseSetting("Jwt:Issuer", "https://integration.presenter-ai.test");
        builder.UseSetting("Jwt:Audience", "presenter-ai-integration");
        builder.UseSetting("Jwt:SecretKey", "integration-only-jwt-signing-key-not-a-credential-123456");
        builder.UseSetting("Jwt:AccessTokenMinutes", "60");
        builder.UseSetting("Jwt:RefreshTokenDays", "30");
        builder.UseSetting("Auth:Dev:Enabled", "true");
        builder.UseSetting("Auth:Dev:UserId", "integration-dev-user");
        builder.UseSetting("Auth:Dev:Email", "integration-dev@example.test");
        builder.UseSetting("Auth:Dev:DisplayName", "Integration Dev User");
        builder.UseSetting("RateLimiting:Enabled", "false");
        builder.UseSetting("Cors:AllowedOrigins:0", "https://app.example.test");
        builder.UseSetting("Content:RootDir", FindRepositoryRoot());
        builder.UseSetting("Content:WebRoot", Path.Combine(Path.GetTempPath(), "presenter-ai-no-web-root"));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "PresenterAi.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("PresenterAi.slnx was not found above the test assembly.");
    }
}
