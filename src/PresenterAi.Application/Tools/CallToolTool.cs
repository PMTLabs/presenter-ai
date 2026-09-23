using System.Text.Json;
using System.Text.Json.Nodes;

namespace PresenterAi.Application.Tools;

public sealed class CallToolTool : ITool
{
    private readonly Func<string, ITool?> _toolLookup;

    public CallToolTool(Func<string, ITool?> toolLookup)
    {
        _toolLookup = toolLookup ?? throw new ArgumentNullException(nameof(toolLookup));
    }

    public string Name => "call_tool";

    public string Description => "Invoke a tool by name with arguments.";

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

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("name", out var nameProp) ||
            nameProp.ValueKind != JsonValueKind.String ||
            !arguments.TryGetProperty("arguments", out var argsProp) ||
            argsProp.ValueKind != JsonValueKind.Object)
        {
            return ToolResult.Failure("Invalid call_tool invocation: 'name' (string) and 'arguments' (object) are required.");
        }

        var toolName = nameProp.GetString()!;
        var tool = _toolLookup(toolName);
        if (tool is null)
        {
            return ToolResult.Failure($"Unknown tool: '{toolName}'.");
        }

        var validation = ToolArgumentValidator.Validate(argsProp, tool.Parameters);
        if (!validation.IsValid)
        {
            return ToolResult.Failure($"Invalid arguments for tool '{toolName}': {validation.ErrorMessage}");
        }

        return await tool.InvokeAsync(argsProp, cancellationToken).ConfigureAwait(false);
    }
}
