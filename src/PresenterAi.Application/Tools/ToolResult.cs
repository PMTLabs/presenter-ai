using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PresenterAi.Application.Tools;

public sealed record ToolResult(bool Ok, string Message, JsonNode? Data = null)
{
    public const int MaxOutputBytes = 4096;

    public string Outcome { get; init; } = Ok ? "ok" : "error";

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
            ["outcome"] = Outcome,
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
        var fallbackJson = new JsonObject
        {
            ["ok"] = Ok,
            ["outcome"] = Outcome,
            ["message"] = truncatedMessage
        }.ToJsonString();
        if (Encoding.UTF8.GetByteCount(fallbackJson) <= MaxOutputBytes)
        {
            return fallbackJson;
        }

        var maxMsgBytes = MaxOutputBytes - 96;
        var msgBytes = Encoding.UTF8.GetBytes(truncatedMessage);
        var safeMessage = Encoding.UTF8.GetString(msgBytes, 0, Math.Min(msgBytes.Length, maxMsgBytes));
        return new JsonObject
        {
            ["ok"] = Ok,
            ["outcome"] = Outcome,
            ["message"] = safeMessage
        }.ToJsonString();
    }
}
