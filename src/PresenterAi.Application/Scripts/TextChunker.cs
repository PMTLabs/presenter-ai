using System.Text.RegularExpressions;

namespace PresenterAi.Application.Scripts;

public static partial class TextChunker
{
    public static IReadOnlyList<string> Chunk(string text, int maxChars)
    {
        var clean = WhitespaceRegex().Replace(text ?? string.Empty, " ").Trim();
        if (clean.Length == 0)
        {
            return Array.Empty<string>();
        }

        if (clean.Length <= maxChars)
        {
            return [clean];
        }

        var sentences = SentenceRegex()
            .Matches(clean)
            .Select(match => match.Value.Trim())
            .Where(sentence => sentence.Length > 0)
            .ToArray();
        if (sentences.Length == 0)
        {
            sentences = [clean];
        }

        var chunks = new List<string>();
        var buffer = string.Empty;

        foreach (var sentence in sentences)
        {
            var pieces = sentence.Length > maxChars
                ? SplitLong(sentence, maxChars)
                : [sentence];

            foreach (var piece in pieces)
            {
                if (buffer.Length == 0)
                {
                    buffer = piece;
                }
                else if (buffer.Length + 1 + piece.Length <= maxChars)
                {
                    buffer += " " + piece;
                }
                else
                {
                    chunks.Add(buffer);
                    buffer = piece;
                }
            }
        }

        if (buffer.Length > 0)
        {
            chunks.Add(buffer);
        }

        return chunks;
    }

    private static IReadOnlyList<string> SplitLong(string sentence, int maxChars)
    {
        var output = new List<string>();
        var rest = sentence;

        while (rest.Length > maxChars)
        {
            var cut = rest.LastIndexOf(' ', maxChars);
            if (cut <= 0)
            {
                cut = maxChars;
            }

            output.Add(rest[..cut].Trim());
            rest = rest[cut..].Trim();
        }

        if (rest.Length > 0)
        {
            output.Add(rest);
        }

        return output;
    }

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("[^.!?]+(?:[.!?]+|$)")]
    private static partial Regex SentenceRegex();
}
