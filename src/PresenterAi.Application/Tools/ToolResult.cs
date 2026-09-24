using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PresenterAi.Application.Tools;

public sealed record ToolResult(bool Ok, string Message, JsonNode? Data = null)
{
    public const int MaxOutputBytes = 4096;

    public static ToolResult Success(string message, JsonNode? data = null) => new(true, message, data);

    public static ToolResult Failure(string message, JsonNode? data = null) => new(false, message, data);

    public static ToolResult Success(string message, JsonElement data) =>
        new(true, message, JsonNode.Parse(data.GetRawText()));

    public static ToolResult Failure(string message, JsonElement data) =>
        new(false, message, JsonNode.Parse(data.GetRawText()));

    public string ToJsonString()
    {
        var node = new JsonObject
        {
            ["ok"] = Ok,
            ["message"] = Message
        };

        if (Data is not null)
        {
            node["data"] = Data.DeepClone();
        }

        var json = node.ToJsonString();
        var bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes <= MaxOutputBytes)
        {
            return json;
        }

        var note = $" [truncated; original data size {bytes} bytes]";
        var truncatedMessage = Message + note;

        var truncatedNode = new JsonObject
        {
            ["ok"] = Ok,
            ["message"] = truncatedMessage
        };

        var fallbackJson = truncatedNode.ToJsonString();
        if (Encoding.UTF8.GetByteCount(fallbackJson) <= MaxOutputBytes)
        {
            return fallbackJson;
        }

        // Search Unicode scalar boundaries, measuring the actual escaped JSON on every step.
        var boundaries = new List<int> { 0 };
        foreach (var rune in truncatedMessage.EnumerateRunes())
            boundaries.Add(boundaries[^1] + rune.Utf16SequenceLength);

        var low = 0;
        var high = boundaries.Count - 1;
        var best = "";
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            var candidate = new JsonObject
            {
                ["ok"] = Ok,
                ["message"] = truncatedMessage[..boundaries[mid]]
            }.ToJsonString();
            if (Encoding.UTF8.GetByteCount(candidate) <= MaxOutputBytes)
            {
                best = candidate;
                low = mid + 1;
            }
            else high = mid - 1;
        }
        return best;
    }
}
