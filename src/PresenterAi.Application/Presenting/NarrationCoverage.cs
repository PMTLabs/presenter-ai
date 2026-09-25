using System.Text;
using System.Text.RegularExpressions;

namespace PresenterAi.Application.Presenting;

/// <summary>
/// Whether a slide's narration was spoken in full, judged from the model's output transcript: the voice stream has no
/// completion event. Conservative by design, since a false "finished" skips content while a false "not finished" only
/// repeats it: at least <see cref="MinCoverage"/> of the narration's words must appear in order, and its last
/// <see cref="TailWords"/> words must appear together. Paraphrase, a cut-off ending or a quoted sentence is not finished.
/// </summary>
public static partial class NarrationCoverage
{
    public const double MinCoverage = 0.9;
    public const int TailWords = 6;

    public static bool SpokenInFull(string narration, string spoken, out double coverage)
    {
        var expected = Words(narration);
        var heard = Words(spoken);
        coverage = expected.Length == 0 ? 0 : (double)CommonInOrder(expected, heard) / expected.Length;
        if (expected.Length == 0 || coverage < MinCoverage) return false;
        return ContainsRun(heard, expected[^Math.Min(TailWords, expected.Length)..]);
    }

    /// <summary>Lower-case words of Unicode letters, marks and digits (Vietnamese keeps its diacritics).</summary>
    public static string[] Words(string text) =>
        WordPattern().Matches(text.Normalize(NormalizationForm.FormC).ToLowerInvariant()).Select(m => m.Value).ToArray();

    /// <summary>Longest common subsequence length, two rows.</summary>
    private static int CommonInOrder(string[] a, string[] b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        foreach (var word in a)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = string.Equals(word, b[j - 1], StringComparison.Ordinal)
                    ? previous[j - 1] + 1
                    : Math.Max(previous[j], current[j - 1]);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static bool ContainsRun(string[] words, string[] run)
    {
        for (var i = 0; i + run.Length <= words.Length; i++)
        {
            var j = 0;
            while (j < run.Length && string.Equals(words[i + j], run[j], StringComparison.Ordinal)) j++;
            if (j == run.Length) return true;
        }

        return false;
    }

    [GeneratedRegex(@"[\p{L}\p{M}\p{N}]+")]
    private static partial Regex WordPattern();
}
