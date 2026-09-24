using System.Text;

namespace PresenterAi.Application.Presenting.VoiceCommands;

public enum VoiceCommandIntent
{
    Pause,
    Resume,
    Next,
    Previous,
    GoTo,
    End,
    Yes,
    No
}

/// <param name="SlideNumber">One-based slide number, present only for GoTo.</param>
public sealed record VoiceCommand(VoiceCommandIntent Intent, int? SlideNumber = null);

/// <summary>Matches complete, assembled utterances only; it does not decide whether a match is eligible.</summary>
public static class VoiceCommandMatcher
{
    private static readonly HashSet<string> Fillers = new(
        new[] { "please", "just", "now", "okay", "ok", "hey", "the", "a" }
            .Concat(ConfirmationLexicon.Languages.SelectMany(language => language.Fillers)),
        StringComparer.Ordinal);

    private static readonly string[] NumberWords =
    {
        "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen",
        "eighteen", "nineteen", "twenty"
    };

    private static readonly (VoiceCommandIntent Intent, string[] Phrases)[] Phrases = new (VoiceCommandIntent Intent, string[] Phrases)[]
    {
        (VoiceCommandIntent.Pause, ["stop", "pause", "wait", "hold on", "stop talking", "stop there", "pause there"]),
        (VoiceCommandIntent.Resume, ["continue", "carry on", "keep going", "go on", "go ahead", "resume", "keep continue"]),
        (VoiceCommandIntent.Next, ["next", "next slide", "go next", "move on"]),
        (VoiceCommandIntent.Previous, ["back", "go back", "previous", "previous slide", "last slide"]),
        (VoiceCommandIntent.End, ["end", "end meeting", "end presentation", "end talk", "finish", "stop presentation"])
    }.Concat(ConfirmationLexicon.Languages.SelectMany(language => new[]
    {
        (VoiceCommandIntent.Yes, language.Yes.ToArray()),
        (VoiceCommandIntent.No, language.No.ToArray())
    })).ToArray();

    public static VoiceCommand? Match(string utterance)
    {
        ArgumentNullException.ThrowIfNull(utterance);
        var words = Normalize(utterance);
        if (words.Count == 0) return null;
        var spaced = string.Join(' ', words);
        var compact = string.Concat(words);

        foreach (var (intent, phrases) in Phrases)
            foreach (var phrase in phrases)
                if (spaced == phrase || compact == phrase.Replace(" ", "", StringComparison.Ordinal))
                    return new VoiceCommand(intent);

        foreach (var prefix in new[] { "go to slide", "slide" })
        {
            var number = spaced.StartsWith(prefix + " ", StringComparison.Ordinal)
                ? spaced[(prefix.Length + 1)..]
                : compact.StartsWith(prefix.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal)
                    ? compact[prefix.Replace(" ", "", StringComparison.Ordinal).Length..]
                    : "";
            if (int.TryParse(number, out var digit) && digit > 0)
                return new VoiceCommand(VoiceCommandIntent.GoTo, digit);
            var wordIndex = Array.IndexOf(NumberWords, number);
            if (wordIndex >= 0) return new VoiceCommand(VoiceCommandIntent.GoTo, wordIndex + 1);
        }
        return null;
    }

    private static List<string> Normalize(string utterance)
    {
        var cleaned = new StringBuilder(utterance.Length);
        // ASR may deliver decomposed diacritics; compose them so "có" matches however it was encoded.
        foreach (var ch in utterance.Normalize(NormalizationForm.FormC).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) cleaned.Append(ch);
            else if (ch != '\'') cleaned.Append(' '); // don't → dont; punctuation otherwise separates words
        }
        var tokens = cleaned.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();
        for (var i = 0; i < tokens.Length; i++)
        {
            if ((tokens[i] == "can" || tokens[i] == "could") && i + 1 < tokens.Length && tokens[i + 1] == "you")
            {
                i++;
                continue;
            }
            if (!Fillers.Contains(tokens[i])) result.Add(tokens[i]);
        }
        return result;
    }
}
