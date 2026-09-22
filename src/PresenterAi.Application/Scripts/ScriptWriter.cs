using System.Text;
using System.Text.Json;

namespace PresenterAi.Application.Scripts;

public static class ScriptWriter
{
    public static string Format(PresentationScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(script.Meta);
        ArgumentNullException.ThrowIfNull(script.Slides);

        var output = new StringBuilder();
        output.AppendLine("---");
        WriteYamlString(output, "id", script.Meta.Id);
        WriteYamlString(output, "title", script.Meta.Title);
        WriteYamlString(output, "deck", script.Meta.Deck);
        WriteYamlString(output, "driver", script.Meta.Driver);
        WriteYamlString(output, "voice", script.Meta.Voice);
        WriteYamlString(output, "context", script.Meta.Context);
        WriteYamlScalar(output, "advanceSilenceMs", script.Meta.AdvanceSilenceMs);
        WriteYamlScalar(output, "chunkChars", script.Meta.ChunkChars);
        output.AppendLine("---");

        for (var i = 0; i < script.Slides.Count; i++)
        {
            if (i > 0)
            {
                output.AppendLine();
            }

            var slide = script.Slides[i];
            output.Append("## Slide ").Append(slide.Number);
            if (slide.Title.Length > 0)
            {
                output.Append(": ").Append(slide.Title);
            }

            output.AppendLine();
            if (slide.Narration.Length > 0)
            {
                output.AppendLine(slide.Narration);
            }

            if (!string.IsNullOrEmpty(slide.Notes))
            {
                output.Append("> notes: ").AppendLine(FirstLine(slide.Notes));
                foreach (var line in RemainingLines(slide.Notes))
                {
                    output.Append("> ").AppendLine(line);
                }
            }
        }

        return output.ToString();
    }

    private static void WriteYamlString(StringBuilder output, string key, string? value)
    {
        output.Append(key).Append(": ").AppendLine(value is null ? "null" : JsonSerializer.Serialize(value));
    }

    private static void WriteYamlScalar(StringBuilder output, string key, int? value)
    {
        output.Append(key).Append(": ").AppendLine(value?.ToString() ?? "null");
    }

    private static string FirstLine(string value)
    {
        var newline = value.IndexOf('\n');
        return newline < 0 ? value : value[..newline].TrimEnd('\r');
    }

    private static IEnumerable<string> RemainingLines(string value)
    {
        var lines = value.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        return lines.Skip(1);
    }
}
