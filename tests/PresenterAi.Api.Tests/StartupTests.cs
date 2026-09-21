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
}
