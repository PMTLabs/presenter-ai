using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PresenterAi.Application.Tools;

public sealed class ToolSessionCatalogue
{
    public const int MaxInlineToolsPayloadBytes = 32 * 1024; // 32 KiB budget per plan §3.1

    private readonly Dictionary<string, ITool> _snapshotTools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ITool> _allAvailableTools = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ITool> _inlineTools = [];
    private readonly List<JsonObject> _inlineDefinitions = [];

    public ToolSessionCatalogue(IEnumerable<ITool> tools, int maxInlineTools = ToolsOptions.DefaultMaxInlineTools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        foreach (var tool in tools)
        {
            var frozen = new SnapshotTool(tool);
            _snapshotTools[tool.Name] = frozen;
            _allAvailableTools[tool.Name] = frozen;
        }

        if (_snapshotTools.Count <= maxInlineTools)
        {
            _inlineTools.AddRange(_snapshotTools.Values);
        }
        else
        {
            var pinnedTools = _snapshotTools.Values.Where(t => t.Pinned).ToList();
            _inlineTools.AddRange(pinnedTools);

            // Snapshot list captured once at catalogue creation time:
            var snapshotList = _snapshotTools.Values.ToList();
            var findTools = new FindToolsTool(() => snapshotList);
            var callTool = new CallToolTool(name => _snapshotTools.GetValueOrDefault(name));

            _inlineTools.Add(findTools);
            _inlineTools.Add(callTool);

            _allAvailableTools[findTools.Name] = findTools;
            _allAvailableTools[callTool.Name] = callTool;
        }

        foreach (var tool in _inlineTools)
        {
            _inlineDefinitions.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = tool.Parameters.DeepClone()
            });
        }

        // Verify 32 KiB budget for inline tool definitions
        var payloadJson = new JsonArray(_inlineDefinitions.Select(d => (JsonNode)d.DeepClone()).ToArray()).ToJsonString();
        var payloadBytes = Encoding.UTF8.GetByteCount(payloadJson);
        if (payloadBytes > MaxInlineToolsPayloadBytes)
        {
            throw new InvalidOperationException(
                $"The serialized tool list exceeds the 32 KiB budget ({payloadBytes} bytes > {MaxInlineToolsPayloadBytes} bytes).");
        }
    }

    public IReadOnlyList<ITool> InlineTools => _inlineTools.Select(t => (ITool)new SnapshotTool(t)).ToArray();

    public IReadOnlyList<ITool> AllTools => _snapshotTools.Values.Select(t => (ITool)new SnapshotTool(t)).ToArray();

    public IReadOnlyList<JsonObject> GetInlineToolDefinitions() => _inlineDefinitions.Select(d => (JsonObject)d.DeepClone()).ToArray();

    private sealed class SnapshotTool(ITool original) : ITool
    {
        private readonly JsonObject _parameters = (JsonObject)original.Parameters.DeepClone();
        public string Name { get; } = original.Name;
        public string Description { get; } = original.Description;
        public IReadOnlyList<string> Tags { get; } = original.Tags.ToArray();
        public bool Pinned { get; } = original.Pinned;
        public JsonObject Parameters => (JsonObject)_parameters.DeepClone();
        public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
            original.InvokeAsync(arguments, cancellationToken);
    }

    public ITool? FindTool(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _allAvailableTools.GetValueOrDefault(name);
    }

    public async Task<ToolResult> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        var tool = FindTool(name);
        if (tool is null)
        {
            return ToolResult.Failure($"Unknown tool: '{name}'.");
        }

        var validation = ToolArgumentValidator.Validate(arguments, tool.Parameters);
        if (!validation.IsValid)
        {
            return ToolResult.Failure($"Invalid arguments for tool '{name}': {validation.ErrorMessage}");
        }

        return await tool.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
    }
}
