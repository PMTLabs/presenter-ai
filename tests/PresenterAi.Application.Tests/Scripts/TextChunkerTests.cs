using PresenterAi.Application.Scripts;
using Xunit;

namespace PresenterAi.Application.Tests.Scripts;

public sealed class TextChunkerTests
{
    [Fact]
    public void Short_text_is_one_chunk_and_empty_is_none()
    {
        Assert.Equal(["Hello world."], TextChunker.Chunk("Hello world.", 100));
        Assert.Empty(TextChunker.Chunk("   ", 100));
    }

    [Fact]
    public void Chunks_long_narration_at_sentence_boundaries_and_round_trips()
    {
        var sentences = Enumerable.Range(0, 40)
            .Select(index => $"Sentence number {index} says something moderately long about topic {index % 7}.");
        var text = string.Join(' ', sentences);
        var chunks = TextChunker.Chunk(text, 300);

        Assert.True(chunks.Count > 1);
        foreach (var chunk in chunks)
        {
            Assert.True(chunk.Length <= 300, $"chunk too long: {chunk.Length}");
            Assert.Matches("[.!?]$", chunk);
        }

        Assert.Equal(Normalise(text), Normalise(string.Join(' ', chunks)));
    }

    [Fact]
    public void Never_cuts_inside_a_word_for_an_overlong_sentence()
    {
        var text = string.Join(' ', Enumerable.Range(0, 200).Select(index => $"w{index}")) + ".";
        var chunks = TextChunker.Chunk(text, 120);

        foreach (var chunk in chunks)
        {
            Assert.True(chunk.Length <= 120);
            Assert.False(chunk.StartsWith(' '));
            Assert.False(chunk.EndsWith(' '));
        }

        Assert.Equal(Normalise(text), Normalise(string.Join(' ', chunks)));
        var rejoined = string.Join(' ', chunks).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["w0", "w1", "w2", "w3", "w4"], rejoined[..5]);
    }

    private static string Normalise(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
