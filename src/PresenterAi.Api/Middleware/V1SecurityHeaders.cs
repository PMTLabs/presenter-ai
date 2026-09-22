namespace PresenterAi.Api.Middleware;

public static class V1SecurityHeaders
{
    private const int MaximumRequestIdLength = 128;

    public static IApplicationBuilder UseV1SecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/v1"))
            {
                await next().ConfigureAwait(false);
                return;
            }

            var requestId = RequestId(context.Request.Headers["X-Request-Id"].ToString());
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["X-Request-Id"] = requestId;
                context.Response.Headers.CacheControl = "no-store";
                return Task.CompletedTask;
            });
            await next().ConfigureAwait(false);
        });

    private static string RequestId(string candidate)
    {
        if (candidate.Length is > 0 and <= MaximumRequestIdLength
            && candidate.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-'))
        {
            return candidate;
        }

        return Guid.NewGuid().ToString("N");
    }
}
