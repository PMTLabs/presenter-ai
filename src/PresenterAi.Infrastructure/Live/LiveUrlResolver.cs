namespace PresenterAi.Infrastructure.Live;

public static class LiveUrlResolver
{
    private static readonly string[] AzureSuffixes = [".azure.com", ".azure.us", ".azure.cn"];

    public static bool IsAzureHost(string host)
    {
        return AzureSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    public static Uri Resolve(string endpoint)
    {
        if (string.IsNullOrEmpty(endpoint))
        {
            throw new InvalidOperationException("resolveLiveUrl: endpoint is empty");
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed))
        {
            throw new InvalidOperationException($"Upstream:Endpoint is not a valid URL: {endpoint}");
        }

        var scheme = parsed.Scheme.ToLowerInvariant() switch
        {
            "http" => "ws",
            "https" => "wss",
            "ws" => "ws",
            "wss" => "wss",
            _ => throw new InvalidOperationException(
                $"Upstream:Endpoint has unsupported scheme: {parsed.Scheme.ToLowerInvariant()}:")
        };

        var builder = new UriBuilder(parsed)
        {
            Scheme = scheme
        };

        if (!builder.Path.Contains("/live/sessions", StringComparison.Ordinal))
        {
            builder.Path = IsAzureHost(parsed.Host)
                ? "/openai/v1/live/sessions"
                : "/v1/live/sessions";
        }

        return builder.Uri;
    }
}
