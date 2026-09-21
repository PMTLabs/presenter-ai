using Microsoft.Extensions.Logging;
using PresenterAi.Application.Presenting;

namespace PresenterAi.Infrastructure.Live;

public interface ILiveSessionFactory
{
    ILiveSession Create(UpstreamRoute route, LiveSessionConfig config);
}

public sealed class LiveSessionFactory(
    TimeProvider timeProvider,
    ILogger<LiveSession> logger,
    LiveSessionOptions options) : ILiveSessionFactory
{
    public ILiveSession Create(UpstreamRoute route, LiveSessionConfig config)
    {
        return new LiveSession(route, config, timeProvider, logger, options);
    }
}
