namespace PresenterAi.Api.Realtime;

/// <summary>Stops the active browser and upstream before the host disposes its services.</summary>
public sealed class PresenterShutdownService(PresenterBridge bridge, ILogger<PresenterShutdownService> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await bridge.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            logger.LogWarning(exception, "Presenter shutdown exceeded its bound");
        }
    }
}
