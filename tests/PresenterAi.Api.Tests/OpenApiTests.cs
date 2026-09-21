using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Api.Tests.Infrastructure;
using PresenterAi.Contracts;

namespace PresenterAi.Api.Tests;

public sealed class OpenApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Document_lists_all_mapped_endpoints()
    {
        using var client = factory.CreateClient();
        var document = await GetDocumentAsync(client);
        var paths = document.RootElement.GetProperty("paths").EnumerateObject()
            .Select(path => path.Name)
            .ToHashSet(StringComparer.Ordinal);
        var routeTemplates = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(template => !string.IsNullOrWhiteSpace(template))
            .Select(template => template!.TrimEnd('/'))
            .Where(template => !template!.StartsWith("/openapi/", StringComparison.Ordinal))
            .Where(template => !template!.StartsWith("/__test/", StringComparison.Ordinal))
            .Where(template => template is not "/ws" and not "{*path:nonfile}")
            .Where(template => !template!.StartsWith("/decks/", StringComparison.Ordinal))
            .ToArray();

        routeTemplates.Should().NotBeEmpty();
        foreach (var routeTemplate in routeTemplates)
            paths.Should().Contain(routeTemplate!);
    }

    [Fact]
    public async Task Every_error_code_has_title_and_status()
    {
        using var client = factory.CreateClient();
        var document = await GetDocumentAsync(client);
        var errorCodes = document.RootElement.GetProperty("x-error-codes").EnumerateArray().ToArray();

        errorCodes.Should().NotBeEmpty();
        foreach (var entry in errorCodes)
        {
            entry.GetProperty("code").GetString().Should().NotBeNullOrWhiteSpace();
            entry.GetProperty("title").GetString().Should().NotBeNullOrWhiteSpace();
            entry.GetProperty("status").GetInt32().Should().BeInRange(400, 599);
        }

        errorCodes.Select(entry => entry.GetProperty("code").GetString())
            .Should().BeEquivalentTo(ErrorCodes.Catalogue.Keys);
    }

    private static async Task<JsonDocument> GetDocumentAsync(HttpClient client)
    {
        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
