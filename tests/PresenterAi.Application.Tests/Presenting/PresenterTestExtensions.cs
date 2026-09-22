using PresenterAi.Application.Presenting;

namespace PresenterAi.Application.Tests.Presenting;

internal static class PresenterTestExtensions
{
    public static async Task<bool> StartAsync(
        this Presenter presenter,
        string id,
        int? fromIndex = null,
        CancellationToken cancellationToken = default)
    {
        var result = await presenter.StartAsync(id, fromIndex, "test-owner", cancellationToken).ConfigureAwait(false);
        return result.Started;
    }
}
