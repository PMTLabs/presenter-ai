using FluentAssertions;
using PresenterAi.Api.Tests.Infrastructure;

namespace PresenterAi.Api.Tests;

public sealed class HealthTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Health_returns_ok()
    {
        var response = await factory.CreateClient().GetAsync("/health");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("{\"status\":\"ok\"}");
    }
}
