using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace PresenterAi.Api.Tests.Infrastructure;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public IReadOnlyDictionary<string, string?>? Overrides { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Upstream:Endpoint", "https://api.openai.com");
        builder.UseSetting("Upstream:Key", "test");
        builder.UseSetting("Auth:Dev:Enabled", "true");
        builder.UseSetting("Auth:Dev:UserId", "test-user");
        builder.UseSetting("Auth:Dev:Email", "test@presenter-ai.local");
        builder.UseSetting("Content:RootDir", FindRepositoryRoot());

        if (Overrides is not null)
        {
            foreach (var setting in Overrides)
            {
                builder.UseSetting(setting.Key, setting.Value);
            }
        }
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
