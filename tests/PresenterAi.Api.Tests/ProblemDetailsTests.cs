using System.Text.Json;
using FluentAssertions;
using PresenterAi.Api.Tests.Infrastructure;
using PresenterAi.Contracts;

namespace PresenterAi.Api.Tests;

public sealed class ProblemDetailsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Unhandled_exception_is_500_with_traceId_and_no_message()
    {
        var response = await factory.CreateClient().GetAsync("/__test/throw");
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        ((int)response.StatusCode).Should().Be(500);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        document.RootElement.GetProperty("code").GetString().Should().Be(ErrorCodes.InternalError);
        document.RootElement.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
        body.Should().NotContain("SECRET-MESSAGE-123");
    }

    [Fact]
    public async Task Domain_exception_maps_code_status_and_title()
    {
        var response = await factory.CreateClient().GetAsync("/__test/domain-error");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        ((int)response.StatusCode).Should().Be(404);
        document.RootElement.GetProperty("code").GetString().Should().Be(ErrorCodes.PresentationNotFound);
        document.RootElement.GetProperty("title").GetString()
            .Should().Be(ErrorCodes.Catalogue[ErrorCodes.PresentationNotFound].Title);
        document.RootElement.GetProperty("type").GetString()
            .Should().Be("https://presenter-ai.dev/errors/presentation.not_found");
    }
}
