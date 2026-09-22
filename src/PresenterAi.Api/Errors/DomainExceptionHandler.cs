using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using PresenterAi.Contracts;
using PresenterAi.Domain.Errors;

namespace PresenterAi.Api.Errors;

public sealed class DomainExceptionHandler(ILogger<DomainExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (code, status, detail, extensions) = exception switch
        {
            DomainException domainException =>
                (domainException.Code, domainException.Status, domainException.Detail, domainException.Extensions),
            BadHttpRequestException badRequest =>
                (BadRequestCode(badRequest.StatusCode), badRequest.StatusCode,
                    "The request could not be processed.", (IReadOnlyDictionary<string, object?>?)null),
            _ => (ErrorCodes.InternalError, StatusCodes.Status500InternalServerError,
                "An unexpected error occurred. Please contact support with the traceId.",
                (IReadOnlyDictionary<string, object?>?)null)
        };
        var traceId = ProblemTrace.Apply(httpContext);
        var title = ErrorCodes.Catalogue.TryGetValue(code, out var entry)
            ? entry.Title
            : ErrorCodes.Catalogue[ErrorCodes.InternalError].Title;

        if (exception is BadHttpRequestException)
            logger.LogDebug("Request binding failed with status {Status} for traceId {TraceId}", status, traceId);
        else if (exception is not DomainException)
            logger.LogError(exception, "Unhandled exception for traceId {TraceId}", traceId);

        var problem = new ProblemDetails
        {
            Type = $"https://presenter-ai.dev/errors/{code}",
            Title = title,
            Status = status,
            Detail = detail,
            Instance = httpContext.Request.Path
        };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = traceId;
        if (extensions is not null)
        {
            foreach (var extension in extensions)
                problem.Extensions[extension.Key] = extension.Value;
        }

        httpContext.Response.StatusCode = status;
        httpContext.Response.ContentType = "application/problem+json";
        await System.Text.Json.JsonSerializer.SerializeAsync(
            httpContext.Response.Body,
            problem,
            cancellationToken: cancellationToken);
        return true;
    }

    private static string BadRequestCode(int status) => status switch
    {
        StatusCodes.Status413PayloadTooLarge => ErrorCodes.ValidationPayloadTooLarge,
        StatusCodes.Status415UnsupportedMediaType => ErrorCodes.ValidationUnsupportedMediaType,
        _ => ErrorCodes.ValidationFailed
    };
}
