namespace PresenterAi.Infrastructure.Live;

public static class UpstreamAuth
{
    public static IReadOnlyDictionary<string, string> Headers(string key, Uri liveUrl)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Authorization"] = $"Bearer {key}"
        };

        if (LiveUrlResolver.IsAzureHost(liveUrl.Host))
        {
            headers["api-key"] = key;
        }

        return headers;
    }
}
