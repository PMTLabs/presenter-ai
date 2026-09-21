namespace PresenterAi.Api.Errors;

public static class NodeErrors
{
    public static IResult Json(int statusCode, string message) =>
        Results.Json(new { error = message }, statusCode: statusCode);
}
