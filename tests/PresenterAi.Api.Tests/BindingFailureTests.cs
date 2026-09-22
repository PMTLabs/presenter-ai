using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using PresenterAi.Api.Errors;
using PresenterAi.Api.Tests.Infrastructure;
using PresenterAi.Contracts;

namespace PresenterAi.Api.Tests;

public sealed class BindingFailureTests
{
    [Theory]
    [InlineData("/v1/auth/sso/google/authorize?code_challenge=challenge&state=state")]
    [InlineData("/v1/auth/sso/google/authorize?redirect_uri=https%3A%2F%2Fapp.example.test&state=state")]
    [InlineData("/v1/auth/sso/google/authorize?redirect_uri=https%3A%2F%2Fapp.example.test&code_challenge=challenge")]
    [InlineData("/v1/auth/sso/google/callback?state=state")]
    [InlineData("/v1/auth/sso/google/callback?code=code")]
    public async Task Missing_query_input_is_a_catalogue_problem(string path)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        await AssertValidationProblem(response);
    }

    [Fact]
    public async Task Missing_token_body_is_a_catalogue_problem()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/v1/auth/sso/token", new StringContent(string.Empty, Encoding.UTF8, "application/json"));
        await AssertValidationProblem(response);
    }

    [Fact]
    public async Task Malformed_token_body_is_a_catalogue_problem()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/sso/token")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request);
        await AssertValidationProblem(response);
    }

    // Neither status is reachable end to end in TestServer: it does not enforce Kestrel's body-size limit, and the
    // routing fallback answers a non-JSON body with 404 before binding runs. So the handler is driven directly.
    [Theory]
    [InlineData(StatusCodes.Status413PayloadTooLarge, ErrorCodes.ValidationPayloadTooLarge)]
    [InlineData(StatusCodes.Status415UnsupportedMediaType, ErrorCodes.ValidationUnsupportedMediaType)]
    public async Task Binding_status_maps_to_its_catalogue_code_without_echoing_input(int status, string code)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var handled = await new DomainExceptionHandler(NullLogger<DomainExceptionHandler>.Instance).TryHandleAsync(
            context, new BadHttpRequestException("raw-client-input", status), CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(status);
        context.Response.ContentType.Should().Be("application/problem+json");
        context.Response.Body.Position = 0;
        var responseText = await new StreamReader(context.Response.Body).ReadToEndAsync();
        JsonNode.Parse(responseText)!["code"]!.GetValue<string>().Should().Be(code);
        responseText.Should().NotContain("raw-client-input");
    }

    [Fact]
    public async Task Malformed_paging_input_is_a_catalogue_problem()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.GetAsync("/v1/presentations?page=abc");
        await AssertValidationProblem(response);
    }

    private static async Task AssertValidationProblem(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        body["code"]!.GetValue<string>().Should().Be(ErrorCodes.ValidationFailed);
        response.Headers.TryGetValues("traceparent", out var traceparent).Should().BeTrue();
        body["traceId"]!.GetValue<string>().Should().Be(traceparent!.Single());
    }
}
