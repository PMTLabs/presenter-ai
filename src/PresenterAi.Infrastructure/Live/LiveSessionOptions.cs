namespace PresenterAi.Infrastructure.Live;

public sealed class LiveSessionOptions
{
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public bool SilencePump { get; init; } = true;

    public bool LogEvents { get; init; }
}
