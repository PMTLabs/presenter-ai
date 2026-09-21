namespace PresenterAi.Infrastructure.Live;

public sealed class UpstreamOptions
{
    public string Endpoint { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string Model { get; set; } = "gpt-live-1";

    public string Voice { get; set; } = "marin";

    public FallbackOptions Fallback { get; set; } = new();

    public sealed class FallbackOptions
    {
        public string Endpoint { get; set; } = "https://api.openai.com";

        public string Key { get; set; } = string.Empty;

        public string Model { get; set; } = "gpt-live-1";
    }
}
