namespace PresenterAi.Infrastructure.Live;

public sealed record UpstreamRoute(
    string Name,
    Uri LiveUrl,
    IReadOnlyDictionary<string, string> Headers,
    string Model,
    string DelegationModel = "");

public sealed record UpstreamRoutes(IReadOnlyList<UpstreamRoute> Upstreams, string Voice)
{
    public static UpstreamRoutes From(UpstreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            throw new InvalidOperationException("Missing required setting: Upstream:Endpoint");
        }

        if (string.IsNullOrWhiteSpace(options.Key))
        {
            throw new InvalidOperationException("Missing required setting: Upstream:Key");
        }

        var primary = CreateRoute(
            "primary",
            options.Endpoint.Trim(),
            options.Key.Trim(),
            options.Model,
            options.DelegationModel,
            "gpt-live-1");

        var routes = new List<UpstreamRoute> { primary };
        var fallbackOptions = options.Fallback ?? new UpstreamOptions.FallbackOptions();
        var fallbackKey = fallbackOptions.Key?.Trim();
        if (!string.IsNullOrWhiteSpace(fallbackKey))
        {
            var fallbackEndpoint = string.IsNullOrEmpty(fallbackOptions.Endpoint)
                ? "https://api.openai.com"
                : fallbackOptions.Endpoint.Trim();
            routes.Add(CreateRoute(
                "fallback",
                fallbackEndpoint,
                fallbackKey,
                fallbackOptions.Model,
                fallbackOptions.DelegationModel,
                "gpt-live-1"));
        }

        return new UpstreamRoutes(routes, Normalise(options.Voice, "marin"));
    }

    private static UpstreamRoute CreateRoute(
        string name,
        string endpoint,
        string key,
        string? model,
        string? delegationModel,
        string defaultModel)
    {
        var liveUrl = LiveUrlResolver.Resolve(endpoint);
        return new UpstreamRoute(
            name,
            liveUrl,
            UpstreamAuth.Headers(key, liveUrl),
            Normalise(model, defaultModel),
            delegationModel?.Trim() ?? string.Empty);
    }

    private static string Normalise(string? value, string defaultValue)
    {
        return string.IsNullOrEmpty(value) ? defaultValue : value.Trim();
    }
}
