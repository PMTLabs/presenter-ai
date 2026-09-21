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

        if (Overrides is not null)
        {
            foreach (var setting in Overrides)
            {
                builder.UseSetting(setting.Key, setting.Value);
            }
        }
    }
}
