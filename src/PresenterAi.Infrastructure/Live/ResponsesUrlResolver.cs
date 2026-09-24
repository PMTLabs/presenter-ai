namespace PresenterAi.Infrastructure.Live;

/// <summary>
/// Derives the out-of-band Responses URL of an upstream route from its live-session URL (plan 010 §4.1), so URL, headers
/// and model always switch together per route: <c>wss</c>→<c>https</c>, <c>ws</c>→<c>http</c>, and a path ending in
/// <c>/live/sessions</c> has that suffix replaced by <c>/responses</c> (Azure <c>/openai/v1/responses</c>, OpenAI
/// <c>/v1/responses</c>, custom prefixes kept). The query string is preserved.
/// </summary>
public static class ResponsesUrlResolver
{
    private const string LiveSuffix = "/live/sessions";
    private const string ResponsesSuffix = "/responses";

    public static Uri Resolve(Uri liveUrl)
    {
        ArgumentNullException.ThrowIfNull(liveUrl);
        if (!liveUrl.IsAbsoluteUri)
        {
            throw new InvalidOperationException("Responses URL: the live URL must be absolute");
        }

        var scheme = liveUrl.Scheme.ToLowerInvariant() switch
        {
            "wss" or "https" => "https",
            "ws" or "http" => "http",
            _ => throw new InvalidOperationException($"Responses URL: unsupported scheme {liveUrl.Scheme}:")
        };

        var builder = new UriBuilder(liveUrl) { Scheme = scheme };
        if (liveUrl.IsDefaultPort)
        {
            builder.Port = -1;
        }

        var path = builder.Path.TrimEnd('/');
        var at = path.LastIndexOf(LiveSuffix, StringComparison.Ordinal);
        builder.Path = at >= 0 && at + LiveSuffix.Length == path.Length
            ? path[..at] + ResponsesSuffix
            : path + ResponsesSuffix;
        return builder.Uri;
    }
}
