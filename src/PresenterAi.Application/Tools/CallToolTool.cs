using System.Text.Json;
using System.Text.Json.Nodes;

namespace PresenterAi.Application.Tools;

public sealed class CallToolTool : ITool
{
    public string Name => "call_tool";

    public string Description => "Resolve a searchable tool by name with arguments.";

    public IReadOnlyList<string> Tags => ["invoke", "tools", "call"];

    public bool Pinned => true;

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The name of the tool to invoke."
            },
            ["arguments"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] = "The arguments to pass to the tool."
            }
        },
        ["required"] = new JsonArray { "name", "arguments" },
        ["additionalProperties"] = false
    };

    public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        Task.FromResult(ToolResult.Failure("call_tool must be resolved by the session catalogue."));
}
