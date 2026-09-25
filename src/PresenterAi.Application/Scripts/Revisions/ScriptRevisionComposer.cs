using System.Text.RegularExpressions;
using PresenterAi.Domain.Errors;

namespace PresenterAi.Application.Scripts.Revisions;

public abstract record ScriptComposition
{
    private ScriptComposition()
    {
    }

    /// <param name="Markdown">The full new script, formatted by <see cref="ScriptWriter"/>.</param>
    /// <param name="Script">The re-parsed new script.</param>
    /// <param name="ChangedSlideIndexes">0-based indexes whose narration differs from the head.</param>
    public sealed record Composed(string Markdown, PresentationScript Script, IReadOnlyList<int> ChangedSlideIndexes)
        : ScriptComposition;

    /// <summary>The structure would not survive; the edit fails as <see cref="ScriptEditErrors.InvalidOutput"/>.</summary>
    public sealed record Failed(string Detail) : ScriptComposition;
}

/// <summary>
/// Pure: applies new narration to the head's slides, formats with <see cref="ScriptWriter.Format"/>, re-parses with
/// <see cref="ScriptParser.Parse"/> and requires the meta, slide count, indexes, numbers, titles, notes and every
/// non-target narration to be unchanged and each target's narration to equal the new text. This is the "only existing
/// slides / structure intact" invariant, independent of the model (plan 010 §4.1).
/// </summary>
public static partial class ScriptRevisionComposer
{
    public static ScriptComposition Compose(PresentationScript head, IReadOnlyList<RevisedSlide> changes)
    {
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(changes);

        var byNumber = new Dictionary<int, string>();
        foreach (var change in changes)
        {
            if (change.Number < 1 || change.Number > head.Slides.Count)
            {
                return new ScriptComposition.Failed($"slide {change.Number} does not exist");
            }

            if (!byNumber.TryAdd(change.Number, Normalise(change.Narration ?? string.Empty)))
            {
                return new ScriptComposition.Failed($"slide {change.Number} changed more than once");
            }
        }

        var slides = head.Slides
            .Select(slide => byNumber.TryGetValue(slide.Number, out var narration) ? slide with { Narration = narration } : slide)
            .ToArray();
        var markdown = ScriptWriter.Format(head with { Slides = slides });

        PresentationScript parsed;
        try
        {
            parsed = ScriptParser.Parse(markdown, head.Meta.Id);
        }
        catch (ScriptParseException exception)
        {
            return new ScriptComposition.Failed($"re-parse failed: {exception.Message}");
        }

        if (parsed.Meta != head.Meta)
        {
            return new ScriptComposition.Failed("frontmatter changed");
        }

        if (parsed.Slides.Count != head.Slides.Count)
        {
            return new ScriptComposition.Failed($"slide count changed from {head.Slides.Count} to {parsed.Slides.Count}");
        }

        var changed = new List<int>();
        for (var i = 0; i < head.Slides.Count; i++)
        {
            var before = head.Slides[i];
            var after = parsed.Slides[i];
            if (after.Index != before.Index || after.Number != before.Number)
            {
                return new ScriptComposition.Failed($"slide {before.Number} was renumbered");
            }

            if (!string.Equals(after.Title, before.Title, StringComparison.Ordinal))
            {
                return new ScriptComposition.Failed($"slide {before.Number} title changed");
            }

            if (!string.Equals(after.Notes ?? string.Empty, before.Notes ?? string.Empty, StringComparison.Ordinal))
            {
                return new ScriptComposition.Failed($"slide {before.Number} notes changed");
            }

            var expected = byNumber.TryGetValue(before.Number, out var narration) ? narration : before.Narration;
            if (!string.Equals(after.Narration, expected, StringComparison.Ordinal))
            {
                return new ScriptComposition.Failed($"slide {before.Number} narration did not survive the re-parse");
            }

            if (!string.Equals(after.Narration, before.Narration, StringComparison.Ordinal))
            {
                changed.Add(i);
            }
        }

        return new ScriptComposition.Composed(markdown, parsed, changed);
    }

    // Mirrors ScriptParser's tidying so harmless whitespace in the model output does not fail the re-parse check.
    private static string Normalise(string narration)
    {
        var lines = narration
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.TrimEnd());
        return BlankRunRegex().Replace(string.Join('\n', lines), "\n\n").Trim();
    }

    [GeneratedRegex("\\n{3,}")]
    private static partial Regex BlankRunRegex();
}
