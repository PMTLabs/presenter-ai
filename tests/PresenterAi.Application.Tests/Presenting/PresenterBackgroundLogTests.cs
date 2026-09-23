using System.Reflection;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Scripts;
using Xunit;

namespace PresenterAi.Application.Tests.Presenting;

public sealed class PresenterBackgroundLogTests
{
    [Fact]
    public async Task Background_failure_log_waits_for_single_reader()
    {
        await using var presenter = new Presenter(
            (_, _) => new FakeSession(),
            (_, id, _) => Task.FromResult(new LoadedPresentation(id,
                new PresentationMeta(id, "Title", "deck", "show", null, null, 2000, 300),
                [new Slide(0, 1, "One", "Narration", "")], null)));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var logged = 0;
        presenter.State += snapshot =>
        {
            if (snapshot.State == "connecting")
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }
        };
        presenter.Log += entry =>
        {
            if (entry.Message.Contains("background probe failed")) Interlocked.Increment(ref logged);
        };
        var starting = presenter.StartAsync("p", null, "owner");
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            var observe = typeof(Presenter).GetMethod("ObserveBackground", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await Task.Run(() => observe.Invoke(presenter,
                [Task.FromException(new InvalidOperationException("probe")), "background probe"]));
            Assert.Equal(0, Volatile.Read(ref logged));
        }
        finally
        {
            release.Set();
        }
        await starting;
        await presenter.WaitUntilIdleAsync();
        Assert.Equal(1, Volatile.Read(ref logged));
    }
}
