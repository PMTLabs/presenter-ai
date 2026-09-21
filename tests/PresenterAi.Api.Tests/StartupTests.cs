using FluentAssertions;
using PresenterAi.Api.Tests.Infrastructure;

namespace PresenterAi.Api.Tests;

public sealed class StartupTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public void Container_validates_on_build()
    {
        var createClient = () => factory.CreateClient();

        createClient.Should().NotThrow();
    }

    [Fact]
    public void Missing_upstream_key_fails_startup()
    {
        using var missingKey = new ApiFactory
        {
            Overrides = new Dictionary<string, string?>
            {
                ["Upstream:Key"] = ""
            }
        };

        var createClient = () => missingKey.CreateClient();

        createClient.Should().Throw<Exception>()
            .Which.ToString().Should().Contain("Missing required setting: Upstream:Key");
    }
}
