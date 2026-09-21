using PresenterAi.Application.Scripts;
using Xunit;

namespace PresenterAi.Application.Tests.Scripts;

public sealed class ScriptWriterTests
{
    [Fact]
    public void Round_trips_ricoh()
    {
        RoundTrip("ricoh-delivery-overview.md", "ricoh-delivery-overview");
    }

    [Fact]
    public void Round_trips_sample()
    {
        RoundTrip("sample.md", "sample");
    }

    private static void RoundTrip(string fixture, string id)
    {
        var original = ScriptParser.Parse(ReadFixture(fixture), id);
        var formatted = ScriptWriter.Format(original);
        var roundTrip = ScriptParser.Parse(formatted, id);

        Assert.Equal(original.Meta, roundTrip.Meta);
        Assert.Equal(original.Slides, roundTrip.Slides);
    }

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
