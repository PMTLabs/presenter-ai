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

    [Fact]
    public void Round_trips_yaml_sensitive_record_values()
    {
        var original = new PresentationScript(
            new PresentationMeta(
                "record-id",
                "Title: \"quoted\" — презентация",
                "deck-name",
                "manual",
                null,
                "context before\n---\ncontext after",
                null,
                1400),
            [
                new Slide(
                    0,
                    1,
                    "Slide: \"quoted\" 日本語",
                    "Narration stays here.",
                    "first line\n- leading item\nthird line"),
            ]);

        var formatted = ScriptWriter.Format(original);
        var roundTrip = ScriptParser.Parse(formatted, original.Meta.Id);

        Assert.Equal(original.Meta, roundTrip.Meta);
        Assert.Equal(original.Slides, roundTrip.Slides);
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
