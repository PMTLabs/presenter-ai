using PresenterAi.Application.Scripts;
using PresenterAi.Domain.Errors;
using Xunit;

namespace PresenterAi.Application.Tests.Scripts;

public sealed class ScriptParserTests
{
    private const string Sample = """
        ---
        title: Demo
        deck: decks/x/index.html
        voice: gleam
        context: ctx.md
        advanceSilenceMs: 1500
        chunkChars: 900
        ---
        intro text that is ignored

        ## Slide 1 — Cover
        Hello there.
        Second line.

        > notes: first note line
        > second note line
        after notes still narration

        ## Slide 2
        Just narration.
        ## Slide 3: Colon title
        > notes: only notes
        """;

    [Fact]
    public void Parses_frontmatter_headings_narration_and_notes()
    {
        var script = ScriptParser.Parse(Sample, "demo");
        var meta = script.Meta;

        Assert.Equal("demo", meta.Id);
        Assert.Equal("Demo", meta.Title);
        Assert.Equal("decks/x/index.html", meta.Deck);
        Assert.Equal("gleam", meta.Voice);
        Assert.Equal("ctx.md", meta.Context);
        Assert.Equal(1500, meta.AdvanceSilenceMs);
        Assert.Equal(900, meta.ChunkChars);
        Assert.Equal("auto", meta.Driver);

        Assert.Equal(3, script.Slides.Count);
        Assert.Equal([0, 1, 2], script.Slides.Select(slide => slide.Index));
        Assert.Equal("Cover", script.Slides[0].Title);
        Assert.Equal("Hello there.\nSecond line.\n\nafter notes still narration", script.Slides[0].Narration);
        Assert.Equal("first note line\nsecond note line", script.Slides[0].Notes);
        Assert.Equal(string.Empty, script.Slides[1].Title);
        Assert.Equal("Just narration.", script.Slides[1].Narration);
        Assert.Equal(string.Empty, script.Slides[1].Notes);
        Assert.Equal("Colon title", script.Slides[2].Title);
        Assert.Equal(string.Empty, script.Slides[2].Narration);
        Assert.Equal("only notes", script.Slides[2].Notes);
    }

    [Fact]
    public void Defaults_chunk_chars_driver_and_title()
    {
        var script = ScriptParser.Parse("---\ndeck: d.html\n---\n## Slide 1\nx", "abc");

        Assert.Equal(1400, script.Meta.ChunkChars);
        Assert.Equal("abc", script.Meta.Title);
        Assert.Null(script.Meta.AdvanceSilenceMs);
    }

    [Fact]
    public void Parses_max_minutes()
    {
        var script = ScriptParser.Parse("---\ndeck: d\nmaxMinutes: 45\n---\n## Slide 1\nx", "p");

        Assert.Equal(45, script.Meta.MaxMinutes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("12abc")]
    [InlineData("1.5")]
    [InlineData("1.0")]
    public void Rejects_non_positive_or_non_integer_max_minutes(string value)
    {
        var exception = Assert.Throws<ScriptParseException>(() =>
            ScriptParser.Parse($"---\ndeck: d\nmaxMinutes: {value}\n---\n## Slide 1\nx", "p"));

        Assert.Contains("maxMinutes must be a positive whole number of minutes", exception.Detail);
    }

    [Fact]
    public void Keeps_above_ceiling_max_minutes()
    {
        var script = ScriptParser.Parse("---\ndeck: d\nmaxMinutes: 241\n---\n## Slide 1\nx", "p");

        Assert.Equal(241, script.Meta.MaxMinutes);
    }

    [Fact]
    public void Headings_must_be_contiguous_from_1()
    {
        var exception = Assert.Throws<ScriptParseException>(() =>
            ScriptParser.Parse("---\ndeck: d\n---\n## Slide 1\na\n## Slide 3\nb", "p"));

        Assert.Equal("presentation.invalid_script", exception.Code);
        Assert.Equal(400, exception.Status);
        Assert.Contains("contiguous", exception.Detail);
        Assert.Contains("Slide 3", exception.Detail);
        Assert.Contains("position 2", exception.Detail);
    }

    [Fact]
    public void Rejects_missing_slides_and_missing_deck()
    {
        var noSlides = Assert.Throws<ScriptParseException>(() =>
            ScriptParser.Parse("---\ndeck: d\n---\nno slides", "p"));
        Assert.Contains("no \"## Slide N\"", noSlides.Detail);

        var noDeck = Assert.Throws<ScriptParseException>(() => ScriptParser.Parse("## Slide 1\nx", "p"));
        Assert.Contains("\"deck\" is required", noDeck.Detail);
    }

    [Fact]
    public void Parses_ricoh_into_11_contiguous_slides()
    {
        var script = ScriptParser.Parse(ReadFixture("ricoh-delivery-overview.md"), "ricoh-delivery-overview");

        Assert.Equal(11, script.Slides.Count);
        Assert.Equal("decks/ricoh/index.html", script.Meta.Deck);
        Assert.Equal("presentations/ricoh-context.md", script.Meta.Context);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11], script.Slides.Select(slide => slide.Number));
        foreach (var slide in script.Slides)
        {
            Assert.NotEmpty(slide.Title);
            Assert.True(slide.Narration.Length > 50, $"slide {slide.Number} has narration");
            Assert.True(slide.Notes?.Length > 0, $"slide {slide.Number} has notes");

            var chunks = TextChunker.Chunk(slide.Narration, script.Meta.ChunkChars);
            Assert.True(chunks.Count <= 3, $"slide {slide.Number} needs {chunks.Count} chunks");
            Assert.Equal(Normalise(slide.Narration), Normalise(string.Join(' ', chunks)));
        }
    }

    [Fact]
    public void Parses_sample_presentation_and_its_long_slide_chunks()
    {
        var script = ScriptParser.Parse(ReadFixture("sample.md"), "sample");

        Assert.Equal(3, script.Slides.Count);
        Assert.Equal("decks/sample/index.html", script.Meta.Deck);
        var chunks = TextChunker.Chunk(script.Slides[1].Narration, script.Meta.ChunkChars);
        Assert.True(chunks.Count >= 2, "slide 2 should need chunking");
        Assert.Equal(Normalise(script.Slides[1].Narration), Normalise(string.Join(' ', chunks)));
    }

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static string Normalise(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
