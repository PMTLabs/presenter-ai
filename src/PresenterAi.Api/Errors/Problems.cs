using System.Diagnostics;
using PresenterAi.Contracts;

namespace PresenterAi.Api.Errors;

public static class Problems
{
    public static IResult NotFound(HttpContext context, string code, string detail) =>
        Create(context, code, StatusCodes.Status404NotFound, detail);

    public static IResult Create(HttpContext context, string code, int status, string detail)
    {
        var title = ErrorCodes.Catalogue.TryGetValue(code, out var entry)
            ? entry.Title
            : ErrorCodes.Catalogue[ErrorCodes.InternalError].Title;
        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
        return Results.Problem(
            type: $"https://presenter-ai.dev/errors/{code}",
            title: title,
            statusCode: status,
            detail: detail,
            instance: context.Request.Path,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["traceId"] = traceId
            });
    }
}
