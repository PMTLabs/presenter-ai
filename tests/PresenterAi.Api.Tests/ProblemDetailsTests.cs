using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PresenterAi.Api.Errors;
using PresenterAi.Api.Tests.Infrastructure;
using PresenterAi.Contracts;
using PresenterAi.Domain.Errors;

namespace PresenterAi.Api.Tests;

public sealed partial class ProblemDetailsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    // W3C traceparent: version-traceId-spanId-flags (conventions §5: the body traceId IS the header value).
    [GeneratedRegex("^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$")]
    private static partial Regex W3CTraceParent();

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

    [Theory]
    [InlineData("/__test/throw")]
    [InlineData("/__test/domain-error")]
    public async Task Problem_response_carries_traceparent_header_equal_to_body_traceId(string path)
    {
        var response = await factory.CreateClient().GetAsync(path);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.Headers.TryGetValues("traceparent", out var values).Should().BeTrue("every Problem Details response sets traceparent");
        var header = values!.Single();
        header.Should().MatchRegex(W3CTraceParent());
        document.RootElement.GetProperty("traceId").GetString().Should().Be(header);
    }

    [Fact]
    public async Task Exception_handler_without_request_activity_still_sets_matching_traceparent()
    {
        // The reviewer's counterexample: no Activity on the request → the old code put TraceIdentifier in the
        // body and no header at all.
        Activity.Current = null;
        var context = NewContext();
        var handler = new DomainExceptionHandler(NullLogger<DomainExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(
            context,
            new ScriptParseException("boom"),
            CancellationToken.None);

        handled.Should().BeTrue();
        var (header, bodyTraceId) = ReadTrace(context);
        header.Should().MatchRegex(W3CTraceParent());
        bodyTraceId.Should().Be(header);
    }

    [Fact]
    public async Task Problems_helper_without_request_activity_sets_matching_traceparent()
    {
        Activity.Current = null;
        var context = NewContext();

        await Problems.NotFound(context, ErrorCodes.PresentationNotFound, "no such deck").ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(404);
        var (header, bodyTraceId) = ReadTrace(context);
        header.Should().MatchRegex(W3CTraceParent());
        bodyTraceId.Should().Be(header);
    }

    [Fact]
    public async Task Problems_helper_uses_the_request_activity_when_present()
    {
        using var activity = new Activity("test-request").SetIdFormat(ActivityIdFormat.W3C).Start();
        var context = NewContext();

        await Problems.NotFound(context, ErrorCodes.PresentationNotFound, "no such deck").ExecuteAsync(context);

        var (header, bodyTraceId) = ReadTrace(context);
        header.Should().Be(activity.Id);
        bodyTraceId.Should().Be(activity.Id);
    }

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        context.Request.Path = "/api/presentations/missing";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static (string Header, string? BodyTraceId) ReadTrace(HttpContext context)
    {
        context.Response.Headers.TryGetValue("traceparent", out var header).Should().BeTrue("traceparent must be set");
        context.Response.Body.Position = 0;
        using var document = JsonDocument.Parse(context.Response.Body);
        return (header.ToString(), document.RootElement.GetProperty("traceId").GetString());
    }
}
