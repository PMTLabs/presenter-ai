using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Api.Tests.Infrastructure;
using PresenterAi.Contracts;

namespace PresenterAi.Api.Tests;

public sealed class OpenApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly IReadOnlyDictionary<string, int> ExpectedErrorStatuses =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["validation.failed"] = 400,
            ["validation.unsupported_media_type"] = 415,
            ["validation.payload_too_large"] = 413,
            ["auth.required"] = 401,
            ["auth.forbidden"] = 403,
            ["auth.sso_provider_disabled"] = 400,
            ["auth.sso_state_invalid"] = 400,
            ["auth.sso_code_used"] = 400,
            ["auth.signup_not_allowed"] = 403,
            ["auth.account_disabled"] = 403,
            ["concurrency.conflict"] = 412,
            ["presentation.not_found"] = 404,
            ["presentation.invalid_script"] = 400,
            ["presentation.slide_count_mismatch"] = 409,
            ["deck.not_found"] = 404,
            ["deck.unsupported_format"] = 415,
            ["deck.no_driver"] = 422,
            ["session.slots_busy"] = 429,
            ["session.already_running"] = 409,
            ["session.quota_exceeded"] = 429,
            ["session.ticket_invalid"] = 401,
            ["upstream.unavailable"] = 503,
            ["upstream.rate_limited"] = 429,
            ["upstream.rejected"] = 502,
            ["generation.quota_exceeded"] = 429,
            ["generation.job_not_found"] = 404,
            ["generation.job_failed"] = 500,
            ["generation.busy"] = 409,
            ["provider.not_found"] = 404,
            ["provider.disabled"] = 409,
            ["provider.test_failed"] = 502,
            ["model.not_found"] = 404,
            ["model.retired"] = 410,
            ["model.not_allowed"] = 403,
            ["document.not_found"] = 404,
            ["document.not_ready"] = 409,
            ["document.too_large"] = 413,
            ["rate_limit.exceeded"] = 429,
            ["internal.error"] = 500,
            ["internal.not_implemented"] = 501,
            ["tools_url_invalid"] = 400,
            ["tools_url_blocked"] = 400,
            ["tools_server_limit"] = 400,
            ["tools_name_invalid"] = 400,
            ["tools_server_not_found"] = 404,
            ["tools_header_invalid"] = 400,
            ["tools_credentials_unavailable"] = 503,
            ["tools_oauth_unsupported"] = 400,
            ["tools_oauth_client_required"] = 400,
            ["tools_unreachable"] = 502,
            ["tools_redirect_refused"] = 400,
            ["tools_oauth_state_invalid"] = 400,
            ["tools_oauth_failed"] = 400,
            ["tools_auth"] = 401,
            ["tools_response_too_large"] = 502,
            ["tools_oauth_invalid_grant"] = 400,
        };

    private static readonly IReadOnlyDictionary<string, RequiredOperation> RequiredOperations =
        new Dictionary<string, RequiredOperation>(StringComparer.Ordinal)
        {
            ["/health"] = new(["200"], RequiresJson200: true, RequiresArray200: false),
            ["/v1/presentations"] = new(["200"], RequiresJson200: true, RequiresArray200: false),
            ["/v1/presentations/{id}"] = new(["200", "400", "404"], RequiresJson200: true, RequiresArray200: false),
            ["/v1/config"] = new(["200"], RequiresJson200: true, RequiresArray200: false),
        };

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
    public async Task Required_operations_have_expected_responses()
    {
        using var client = factory.CreateClient();
        var document = await GetDocumentAsync(client);
        var paths = document.RootElement.GetProperty("paths");

        foreach (var (route, required) in RequiredOperations)
        {
            paths.TryGetProperty(route, out var path).Should().BeTrue($"required OpenAPI path {route}");
            path.TryGetProperty("get", out var operation).Should().BeTrue($"required GET operation for {route}");
            var responses = operation.GetProperty("responses");
            var responseCodes = responses.EnumerateObject().Select(response => response.Name);
            responseCodes.Should().Contain(required.ResponseCodes);

            foreach (var responseCode in required.ResponseCodes)
                responses.TryGetProperty(responseCode, out _).Should().BeTrue($"{route} GET response {responseCode}");

            if (required.RequiresJson200)
            {
                var content = responses.GetProperty("200").GetProperty("content");
                content.TryGetProperty("application/json", out _).Should().BeTrue(
                    $"{route} GET 200 should declare application/json");
            }

            if (required.RequiresArray200)
            {
                var schema = responses.GetProperty("200")
                    .GetProperty("content")
                    .GetProperty("application/json")
                    .GetProperty("schema");
                schema.GetProperty("type").GetString().Should().Be("array");
            }
        }
    }

    [Fact]
    public async Task Every_error_code_has_independent_status_and_description()
    {
        using var client = factory.CreateClient();
        var document = await GetDocumentAsync(client);
        var errorCodes = document.RootElement.GetProperty("x-error-codes").EnumerateArray().ToArray();
        var descriptions = ErrorCodeDescriptions();

        ErrorCodes.Catalogue.Keys.Should().BeEquivalentTo(ExpectedErrorStatuses.Keys);
        descriptions.Keys.Should().BeEquivalentTo(ExpectedErrorStatuses.Keys);
        foreach (var (code, expectedStatus) in ExpectedErrorStatuses)
        {
            ErrorCodes.Catalogue.Should().ContainKey(code);
            ErrorCodes.Catalogue[code].Status.Should().Be(expectedStatus);
            ErrorCodes.Catalogue[code].Title.Should().Be(descriptions[code]);
        }

        errorCodes.Should().NotBeEmpty();
        errorCodes.Select(entry => entry.GetProperty("code").GetString())
            .Should().BeEquivalentTo(ExpectedErrorStatuses.Keys);
        foreach (var entry in errorCodes)
        {
            var code = entry.GetProperty("code").GetString();
            code.Should().NotBeNullOrWhiteSpace();
            ExpectedErrorStatuses.Should().ContainKey(code!);
            entry.GetProperty("status").GetInt32().Should().Be(ExpectedErrorStatuses[code!]);
            entry.GetProperty("title").GetString().Should().Be(descriptions[code!]);
            entry.GetProperty("title").GetString().Should().NotBeNullOrWhiteSpace();
        }
    }

    private static IReadOnlyDictionary<string, string> ErrorCodeDescriptions() =>
        typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(string))
            .ToDictionary(
                field => (string)field.GetRawConstantValue()!,
                field => field.GetCustomAttribute<DescriptionAttribute>()?.Description
                    ?? throw new InvalidOperationException($"Missing DescriptionAttribute on {field.Name}"),
                StringComparer.Ordinal);

    [Fact]
    public async Task Checked_in_document_matches_live_document()
    {
        using var client = factory.CreateClient();
        var snapshotPath = Path.Combine(FindRepositoryRoot(), "web", "shared", "openapi", "v1.json");
        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
        {
            var liveNode = JsonNode.Parse(await client.GetStringAsync("/openapi/v1.json"))!;
            if (liveNode is JsonObject root && root["servers"] is JsonArray serversArr && serversArr.Count > 0)
            {
                if (serversArr[0] is JsonObject s) s["url"] = "http://localhost:47913/";
            }
            await File.WriteAllTextAsync(snapshotPath, liveNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        var live = JsonNode.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var snapshot = JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath));

        JsonNode.DeepEquals(Normalize(live), Normalize(snapshot)).Should().BeTrue("the checked-in OpenAPI snapshot must match the live document");
    }

    private static JsonNode? Normalize(JsonNode? document)
    {
        if (document is JsonObject root && root["servers"] is JsonArray servers)
        {
            foreach (var server in servers.OfType<JsonObject>()) server["url"] = "<server>";
        }

        return document;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PresenterAi.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("PresenterAi.slnx was not found above the test assembly.");
    }

    private static async Task<JsonDocument> GetDocumentAsync(HttpClient client)
    {
        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private sealed record RequiredOperation(
        IReadOnlyList<string> ResponseCodes,
        bool RequiresJson200,
        bool RequiresArray200);
}
