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

        // Extreme edge case: message itself is huge
        var maxMsgBytes = MaxOutputBytes - 64;
        var msgBytes = Encoding.UTF8.GetBytes(truncatedMessage);
        var safeMessage = Encoding.UTF8.GetString(msgBytes, 0, Math.Min(msgBytes.Length, maxMsgBytes));

        return new JsonObject
        {
            ["ok"] = Ok,
            ["message"] = safeMessage
        }.ToJsonString();
    }
}
