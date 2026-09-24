using System.Text;

namespace PresenterAi.Application.Presenting.VoiceCommands;

/// <param name="Language">The ISO language code (e.g. "en", "vi").</param>
/// <param name="Phrase">The matched phrase in its canonical lexicon form.</param>
/// <param name="Question">The question text preceding the phrase, with trailing punctuation trimmed.</param>
public sealed record AskDonePhrase(string Language, string Phrase, string Question);

/// <summary>
/// Ask-done phrases per language (plan 011 §4.1). Matched only as an end-of-utterance suffix (after normalisation).
/// The matched phrase is stripped to yield the question text. Adding a language is one entry here plus its tests in
/// <c>AskDoneLexiconTests</c>.
/// </summary>
public static class AskDoneLexicon
{
    /// <param name="Fillers">Politeness particles dropped before matching (e.g. "please", Vietnamese "ạ").</param>
    public sealed record Language(string Code, IReadOnlyList<string> Phrases, IReadOnlyList<string> Fillers);

    public static IReadOnlyList<Language> Languages { get; } =
    [
        new("en", ["ask done", "done asking", "that's my question", "I'm done", "over to you"], ["please"]),
        new("vi", ["hỏi xong", "tôi hỏi xong rồi", "xong rồi", "đó là câu hỏi của tôi"], ["ạ"])
    ];

    private readonly record struct Token(string Normalized, int StartIndex, int EndIndex);

    private sealed record PreparedPhrase(string LanguageCode, string CanonicalPhrase, string[] Tokens);

    private static readonly PreparedPhrase[] PreparedPhrases = Languages
        .SelectMany(lang => lang.Phrases.Select(phrase => new PreparedPhrase(
            lang.Code,
            phrase,
            Tokenize(phrase).Select(t => t.Normalized).ToArray())))
        .OrderByDescending(p => p.Tokens.Length)
        .ThenByDescending(p => p.CanonicalPhrase.Length)
        .ToArray();

    public static AskDonePhrase? Match(string utterance)
    {
        ArgumentNullException.ThrowIfNull(utterance);
        if (string.IsNullOrWhiteSpace(utterance)) return null;

        var composed = utterance.Normalize(NormalizationForm.FormC);
        var tokens = Tokenize(composed);
        if (tokens.Count == 0) return null;

        // Check every language. The longest phrase equal to the utterance's last tokens wins.
        AskDonePhrase? bestMatch = null;
        var bestTokenCount = -1;
        var bestPhraseLength = -1;

        foreach (var language in Languages)
        {
            var fillers = new HashSet<string>(
                language.Fillers.Select(f => f.Normalize(NormalizationForm.FormC).ToLowerInvariant()),
                StringComparer.Ordinal);

            // Strip trailing fillers for this language
            var effectiveEnd = tokens.Count;
            while (effectiveEnd > 0 && fillers.Contains(tokens[effectiveEnd - 1].Normalized))
            {
                effectiveEnd--;
            }

            if (effectiveEnd == 0) continue;

            // Check candidate phrases for this language
            foreach (var candidate in PreparedPhrases)
            {
                if (candidate.LanguageCode != language.Code) continue;

                var k = candidate.Tokens.Length;
                if (k == 0 || k > effectiveEnd) continue;

                // Match suffix ending at effectiveEnd
                var matches = true;
                for (var i = 0; i < k; i++)
                {
                    if (tokens[effectiveEnd - k + i].Normalized != candidate.Tokens[i])
                    {
                        matches = false;
                        break;
                    }
                }

                if (!matches) continue;

                // Check if this candidate is longer than previous best
                if (k > bestTokenCount || (k == bestTokenCount && candidate.CanonicalPhrase.Length > bestPhraseLength))
                {
                    bestTokenCount = k;
                    bestPhraseLength = candidate.CanonicalPhrase.Length;

                    var firstTokenIndex = effectiveEnd - k;
                    var firstCharIndex = tokens[firstTokenIndex].StartIndex;
                    var questionRaw = composed[..firstCharIndex];
                    var question = CleanQuestion(questionRaw);

                    bestMatch = new AskDonePhrase(candidate.LanguageCode, candidate.CanonicalPhrase, question);
                }
            }
        }

        return bestMatch;
    }

    private static string CleanQuestion(string text)
    {
        var span = text.AsSpan().Trim();
        while (!span.IsEmpty && (char.IsPunctuation(span[^1]) || char.IsWhiteSpace(span[^1])))
        {
            span = span[..^1].TrimEnd();
        }
        return span.ToString();
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var sb = new StringBuilder();
        var startIndex = -1;
        var endIndex = -1;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsLetterOrDigit(ch))
            {
                if (startIndex < 0) startIndex = i;
                endIndex = i + 1;
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (ch == '\'' || ch == '’')
            {
                // Apostrophes are dropped, but do not separate word tokens.
                if (startIndex >= 0)
                {
                    endIndex = i + 1;
                }
            }
            else
            {
                if (sb.Length > 0)
                {
                    tokens.Add(new Token(sb.ToString(), startIndex, endIndex));
                    sb.Clear();
                    startIndex = -1;
                    endIndex = -1;
                }
            }
        }

        if (sb.Length > 0)
        {
            tokens.Add(new Token(sb.ToString(), startIndex, endIndex));
        }

        return tokens;
    }
}
