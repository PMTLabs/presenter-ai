using System.Globalization;
using System.Text.RegularExpressions;
using PresenterAi.Domain.Errors;
using YamlDotNet.Serialization;
using YamlDotNet.Core;

namespace PresenterAi.Application.Scripts;

public static partial class ScriptParser
{
    private const int DefaultChunkChars = 1400;

    public static PresentationScript Parse(string markdown, string id)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(id);

        var (frontMatter, content) = SplitFrontMatter(markdown);
        var data = ParseFrontMatter(frontMatter);
        var meta = NormaliseMeta(data, id);
        var sections = new List<Section>();
        Section? current = null;

        foreach (var line in SplitLines(content))
        {
            var match = SlideHeadingRegex().Match(line);
            if (match.Success)
            {
                current = new Section(
                    int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    match.Groups[2].Success ? match.Groups[2].Value.Trim() : string.Empty);
                sections.Add(current);
            }
            else if (current is not null)
            {
                current.Lines.Add(line);
            }
        }

        if (sections.Count == 0)
        {
            throw Invalid(meta.Id, "no \"## Slide N\" sections found");
        }

        for (var i = 0; i < sections.Count; i++)
        {
            if (sections[i].Number != i + 1)
            {
                throw Invalid(
                    meta.Id,
                    $"slide headings must be contiguous from 1; found \"## Slide {sections[i].Number}\" at position {i + 1}");
            }
        }

        if (string.IsNullOrEmpty(meta.Deck))
        {
            throw Invalid(meta.Id, "frontmatter \"deck\" is required");
        }

        var slides = sections
            .Select((section, index) =>
            {
                var (narration, notes) = SplitNotes(section.Lines);
                return new Slide(index, section.Number, section.Title, narration, notes);
            })
            .ToArray();

        return new PresentationScript(meta, slides);
    }

    private static PresentationMeta NormaliseMeta(IReadOnlyDictionary<string, object?> data, string requestedId)
    {
        var dataId = Get(data, "id");
        var metaId = IsTruthy(requestedId)
            ? requestedId
            : IsTruthy(dataId)
                ? ToJavaScriptString(dataId!)
                : "presentation";

        var titleValue = Get(data, "title");
        var title = IsTruthy(titleValue)
            ? ToJavaScriptString(titleValue!)
            : IsTruthy(requestedId)
                ? requestedId
                : "Presentation";

        var deckValue = Get(data, "deck");
        var driverValue = Get(data, "driver");
        var voiceValue = Get(data, "voice");
        var contextValue = Get(data, "context");
        var chunkChars = ToInt(Get(data, "chunkChars")) ?? DefaultChunkChars;
        int? maxMinutes = null;
        if (data.ContainsKey("maxMinutes"))
        {
            var value = Get(data, "maxMinutes");
            var isIntegerScalar = value is string or sbyte or byte or short or ushort or int or uint or long or ulong;
            var text = value is null ? string.Empty : ToJavaScriptString(value);
            if (!isIntegerScalar
                || !Regex.IsMatch(text, "^[0-9]+$", RegexOptions.None)
                || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedMaxMinutes)
                || parsedMaxMinutes < 1)
            {
                throw Invalid(metaId, "maxMinutes must be a positive whole number of minutes");
            }

            maxMinutes = parsedMaxMinutes;
        }

        if (chunkChars < 200)
        {
            throw Invalid(metaId, "chunkChars must be >= 200");
        }

        return new PresentationMeta(
            metaId,
            title,
            IsTruthy(deckValue) ? ToJavaScriptString(deckValue!) : string.Empty,
            IsTruthy(driverValue) ? ToJavaScriptString(driverValue!) : "auto",
            IsTruthy(voiceValue) ? ToJavaScriptString(voiceValue!) : null,
            IsTruthy(contextValue) ? ToJavaScriptString(contextValue!) : null,
            ToInt(Get(data, "advanceSilenceMs")),
            chunkChars,
            maxMinutes);
    }

    private static Dictionary<string, object?> ParseFrontMatter(string? frontMatter)
    {
        if (string.IsNullOrEmpty(frontMatter))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        try
        {
            var deserializer = new DeserializerBuilder().Build();
            return deserializer.Deserialize<Dictionary<string, object?>>(frontMatter)
                ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        catch (YamlException exception)
        {
            throw new ScriptParseException(exception.Message);
        }
    }

    private static (string? FrontMatter, string Content) SplitFrontMatter(string markdown)
    {
        var lines = SplitLines(markdown).ToArray();
        if (lines.Length == 0 || lines[0] != "---")
        {
            return (null, markdown);
        }

        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i] is "---" or "...")
            {
                var frontMatter = string.Join('\n', lines[1..i]);
                var content = i + 1 < lines.Length ? string.Join('\n', lines[(i + 1)..]) : string.Empty;
                return (frontMatter, content);
            }
        }

        return (null, markdown);
    }

    private static IEnumerable<string> SplitLines(string text) =>
        Regex.Split(text, "\\r?\\n", RegexOptions.None);

    private static (string Narration, string Notes) SplitNotes(IEnumerable<string> lines)
    {
        var narration = new List<string>();
        var notes = new List<string>();
        var inNotes = false;

        foreach (var raw in lines)
        {
            var start = NotesStartRegex().Match(raw);
            if (start.Success)
            {
                inNotes = true;
                if (start.Groups[1].Value.Length > 0)
                {
                    notes.Add(start.Groups[1].Value);
                }

                continue;
            }

            var blockquote = BlockquoteRegex().Match(raw);
            if (inNotes && blockquote.Success)
            {
                notes.Add(blockquote.Groups[1].Value);
                continue;
            }

            inNotes = false;
            narration.Add(raw);
        }

        return (Tidy(string.Join('\n', narration)), Tidy(string.Join('\n', notes)));
    }

    private static string Tidy(string text)
    {
        var lines = text
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.TrimEnd());
        var tidy = string.Join('\n', lines);
        tidy = Regex.Replace(tidy, "\\n{3,}", "\n\n");
        return tidy.Trim();
    }

    private static int? ToInt(object? value)
    {
        if (value is null || value is string { Length: 0 })
        {
            return null;
        }

        var text = ToJavaScriptString(value);
        var match = Regex.Match(text, "^[\\s]*([+-]?\\d+)");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result))
        {
            return null;
        }

        return result;
    }

    private static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool boolean => boolean,
        string text => text.Length > 0,
        sbyte number => number != 0,
        byte number => number != 0,
        short number => number != 0,
        ushort number => number != 0,
        int number => number != 0,
        uint number => number != 0,
        long number => number != 0,
        ulong number => number != 0,
        float number => number != 0 && !float.IsNaN(number),
        double number => number != 0 && !double.IsNaN(number),
        decimal number => number != 0,
        _ => true
    };

    private static string ToJavaScriptString(object value) => value switch
    {
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty
    };

    private static object? Get(IReadOnlyDictionary<string, object?> data, string key) =>
        data.TryGetValue(key, out var value) ? value : null;

    private static ScriptParseException Invalid(string id, string message) =>
        new($"{id}: {message}");

    [GeneratedRegex("^##\\s+Slide\\s+(\\d+)\\s*(?:[—–:-]\\s*(.*?))?\\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SlideHeadingRegex();

    [GeneratedRegex("^>\\s*notes:\\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex NotesStartRegex();

    [GeneratedRegex("^>\\s?(.*)$")]
    private static partial Regex BlockquoteRegex();

    private sealed class Section(int number, string title)
    {
        public int Number { get; } = number;
        public string Title { get; } = title;
        public List<string> Lines { get; } = [];
    }
}
