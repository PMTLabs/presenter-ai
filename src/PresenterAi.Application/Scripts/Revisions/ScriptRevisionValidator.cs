using System.Text.RegularExpressions;

namespace PresenterAi.Application.Scripts.Revisions;

public abstract record ScriptRevisionValidation
{
    private ScriptRevisionValidation()
    {
    }

    /// <summary>Only the targets whose narration differs from the head (trimmed), and the trimmed, capped summary.</summary>
    public sealed record Valid(IReadOnlyList<RevisedSlide> Changed, string Summary) : ScriptRevisionValidation;

    /// <summary>The output is rejected (<see cref="ScriptEditErrors.InvalidOutput"/>); <see cref="Detail"/> is for logs.</summary>
    public sealed record Invalid(string Detail) : ScriptRevisionValidation;
}

/// <summary>
/// Checks reviser output against the head it was asked about (plan 010 §4.1), independent of the model: numbers unique,
/// requested and in range; narration non-blank, at most <see cref="MaxNarrationChars"/>, no slide headings or
/// blockquotes, no instruction-like markers; summary non-blank; and at least one target actually changed.
/// </summary>
public static partial class ScriptRevisionValidator
{
    public const int MaxNarrationChars = 8_000;
    public const int MaxSummaryChars = 200;

    /// <param name="head">Slides of the head the reviser was given.</param>
    /// <param name="targetNumbers">The 1-based slide numbers the reviser was asked to change.</param>
    /// <param name="output">The reviser's successful result.</param>
    public static ScriptRevisionValidation Validate(
        IReadOnlyList<Slide> head,
        IReadOnlyCollection<int> targetNumbers,
        ScriptRevisionResult.Ok output)
    {
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(targetNumbers);
        ArgumentNullException.ThrowIfNull(output);

        var summary = (output.Summary ?? string.Empty).Trim();
        if (summary.Length == 0)
        {
            return new ScriptRevisionValidation.Invalid("summary is blank");
        }

        if (summary.Length > MaxSummaryChars)
        {
            summary = summary[..MaxSummaryChars].TrimEnd();
        }

        var slides = output.Slides ?? [];
        if (slides.Count == 0)
        {
            return new ScriptRevisionValidation.Invalid("no slides returned");
        }

        var seen = new HashSet<int>();
        foreach (var slide in slides)
        {
            if (!seen.Add(slide.Number))
            {
                return new ScriptRevisionValidation.Invalid($"slide {slide.Number} returned more than once");
            }

            if (slide.Number < 1 || slide.Number > head.Count)
            {
                return new ScriptRevisionValidation.Invalid($"slide {slide.Number} is out of range 1..{head.Count}");
            }

            if (!targetNumbers.Contains(slide.Number))
            {
                return new ScriptRevisionValidation.Invalid($"slide {slide.Number} was not requested");
            }
        }

        var changed = new List<RevisedSlide>();
        foreach (var slide in slides)
        {
            var narration = (slide.Narration ?? string.Empty).Trim();
            if (narration.Length == 0)
            {
                return new ScriptRevisionValidation.Invalid($"slide {slide.Number} narration is blank");
            }

            if (narration.Length > MaxNarrationChars)
            {
                return new ScriptRevisionValidation.Invalid(
                    $"slide {slide.Number} narration exceeds {MaxNarrationChars} characters");
            }

            var marker = FindForbiddenMarker(narration);
            if (marker is not null)
            {
                return new ScriptRevisionValidation.Invalid($"slide {slide.Number} narration contains {marker}");
            }

            var before = head[slide.Number - 1].Narration.Trim();
            if (!string.Equals(narration, before, StringComparison.Ordinal))
            {
                changed.Add(new RevisedSlide(slide.Number, narration));
            }
        }

        return changed.Count == 0
            ? new ScriptRevisionValidation.Invalid("no requested slide changed")
            : new ScriptRevisionValidation.Valid(changed, summary);
    }

    /// <summary>A short description of the first structural or instruction-like marker, or null when there is none.</summary>
    public static string? FindForbiddenMarker(string narration)
    {
        ArgumentNullException.ThrowIfNull(narration);
        if (narration.Contains("```", StringComparison.Ordinal) || narration.Contains("~~~", StringComparison.Ordinal))
        {
            return "a code fence";
        }

        if (IgnoreInstructionsRegex().IsMatch(narration))
        {
            return "an instruction override";
        }

        if (InstructionTagRegex().IsMatch(narration))
        {
            return "an instruction tag";
        }

        foreach (var raw in narration.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("## Slide", StringComparison.OrdinalIgnoreCase))
            {
                return "a slide heading";
            }

            if (line.StartsWith('>'))
            {
                return "a blockquote line";
            }

            if (RoleLineRegex().IsMatch(line))
            {
                return "a role-prefixed line";
            }

            if (StageDirectionRegex().IsMatch(line))
            {
                return "a stage direction";
            }
        }

        return null;
    }

    [GeneratedRegex(@"^(?:system|assistant|user|developer)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RoleLineRegex();

    [GeneratedRegex(
        @"\bignore\s+(?:all\s+(?:the\s+)?(?:previous\s+|prior\s+|above\s+)?|previous\s+|the\s+above\s+|prior\s+)instructions\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IgnoreInstructionsRegex();

    [GeneratedRegex(
        @"</?\s*(?:system|instructions?|assistant|user|developer)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstructionTagRegex();

    [GeneratedRegex(@"^(?:\[[^\]]*\]|\([^)]*\))$", RegexOptions.CultureInvariant)]
    private static partial Regex StageDirectionRegex();
}
