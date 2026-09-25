using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PresenterAi.Application.Tools;

public sealed record ToolResolution(ITool? Tool, JsonElement Arguments, ToolResult? Error)
{
    public bool IsResolved => Tool is not null && Error is null;

    public static ToolResolution Resolved(ITool tool, JsonElement arguments) => new(tool, arguments, null);

    public static ToolResolution Failed(string message) =>
        new(null, default, ToolResult.Failure(message));
}

public sealed class ToolSessionCatalogue
{
    public const int MaxInlineToolsPayloadBytes = 32 * 1024;

    private readonly Dictionary<string, ITool> _snapshotTools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ITool> _allAvailableTools = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ITool> _inlineTools = [];
    private readonly List<JsonObject> _inlineDefinitions = [];
    private readonly List<string> _notes = [];

    public ToolSessionCatalogue(IEnumerable<ITool> tools, int maxInlineTools = ToolsOptions.DefaultMaxInlineTools)
        : this(tools, [], maxInlineTools)
    {
    }

    private ToolSessionCatalogue(IEnumerable<ITool> presenterTools, IEnumerable<ITool> sessionTools, int maxInlineTools)
    {
        ArgumentNullException.ThrowIfNull(presenterTools);
        ArgumentNullException.ThrowIfNull(sessionTools);

        var presenter = presenterTools.Select(t => (ITool)new SnapshotTool(t)).ToList();
        var session = sessionTools.Select(t => (ITool)new SnapshotTool(t)).ToList();
        foreach (var tool in presenter.Concat(session))
        {
            if (!_snapshotTools.TryAdd(tool.Name, tool))
            {
                throw new InvalidOperationException($"Tool with name '{tool.Name}' is already registered.");
            }
            _allAvailableTools.Add(tool.Name, tool);
        }

        var all = presenter.Concat(session).ToList();
        var overflow = all.Count > maxInlineTools ||
            GetPayloadBytes(all.Select(CreateDefinition)) > MaxInlineToolsPayloadBytes;
        if (!overflow)
        {
            AddInline(presenter);
            foreach (var tool in session)
            {
                TryAddSessionInline(tool);
            }
        }
        else
        {
            if (all.Count <= maxInlineTools)
            {
                _notes.Add("Tools use discovery because the inline tool payload exceeded the 32 KiB budget.");
            }

            var pinned = presenter.Where(t => t.Pinned).ToList();
            AddInline(pinned);

            var snapshotList = all;
            var findTools = new FindToolsTool(() => snapshotList);
            var callTool = new CallToolTool();
            AddInline([findTools, callTool]);
            _allAvailableTools[findTools.Name] = findTools;
            _allAvailableTools[callTool.Name] = callTool;
        }
    }

    public static ToolSessionCatalogue Build(
        ToolRegistry registry,
        IEnumerable<ITool>? sessionTools = null,
        int maxInlineTools = ToolsOptions.DefaultMaxInlineTools)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return new ToolSessionCatalogue(registry.GetAllTools(), sessionTools ?? [], maxInlineTools);
    }

    public IReadOnlyList<ITool> InlineTools => _inlineTools.Select(t => (ITool)new SnapshotTool(t)).ToArray();

    public IReadOnlyList<ITool> AllTools => _snapshotTools.Values.Select(t => (ITool)new SnapshotTool(t)).ToArray();

    public IReadOnlyList<string> Notes => _notes.ToArray();

    public IReadOnlyList<JsonObject> GetInlineToolDefinitions() => _inlineDefinitions.Select(d => (JsonObject)d.DeepClone()).ToArray();

    private sealed class SnapshotTool(ITool original) : ITool
    {
        private readonly JsonObject _parameters = (JsonObject)original.Parameters.DeepClone();
        public string Name { get; } = original.Name;
        public string Description { get; } = original.Description;
        public IReadOnlyList<string> Tags { get; } = original.Tags.ToArray();
        public bool Pinned { get; } = original.Pinned;
        public bool RequiresConfirmation { get; } = original.RequiresConfirmation;
        public TimeSpan Timeout { get; } = original.Timeout;
        public string Source { get; } = original.Source;
        public string Title { get; } = original.Title;
        public string? ConfirmationQuestion { get; } = original.ConfirmationQuestion;
        public JsonObject Parameters => (JsonObject)_parameters.DeepClone();
        public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
            original.InvokeAsync(arguments, cancellationToken);
    }

    public ITool? FindTool(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _allAvailableTools.GetValueOrDefault(name);
    }

    public ToolResolution Resolve(string callName, JsonElement arguments)
    {
        ArgumentNullException.ThrowIfNull(callName);
        if (!_allAvailableTools.TryGetValue(callName, out var outer))
        {
            return ToolResolution.Failed($"Unknown tool: '{callName}'.");
        }

        if (callName.Equals("call_tool", StringComparison.OrdinalIgnoreCase))
        {
            var outerValidation = ToolArgumentValidator.Validate(arguments, outer.Parameters);
            if (!outerValidation.IsValid)
            {
                return ToolResolution.Failed($"Invalid call_tool invocation: {outerValidation.ErrorMessage}");
            }

            var name = arguments.GetProperty("name").GetString()!;
            var innerArguments = arguments.GetProperty("arguments");
            if (!_snapshotTools.TryGetValue(name, out var target))
            {
                return ToolResolution.Failed($"Unknown tool: '{name}'.");
            }

            var validation = ToolArgumentValidator.Validate(innerArguments, target.Parameters);
            if (!validation.IsValid)
            {
                return ToolResolution.Failed($"Invalid arguments for tool '{name}': {validation.ErrorMessage}");
            }

            return ToolResolution.Resolved(target, innerArguments.Clone());
        }

        var result = ToolArgumentValidator.Validate(arguments, outer.Parameters);
        return result.IsValid
            ? ToolResolution.Resolved(outer, arguments.Clone())
            : ToolResolution.Failed($"Invalid arguments for tool '{callName}': {result.ErrorMessage}");
    }

    public async Task<ToolResult> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var resolution = Resolve(name, arguments);
        if (!resolution.IsResolved)
        {
            return resolution.Error!;
        }

        return await resolution.Tool!.InvokeAsync(resolution.Arguments, cancellationToken).ConfigureAwait(false);
    }

    private void TryAddSessionInline(ITool tool)
    {
        var definition = CreateDefinition(tool);
        var candidate = _inlineDefinitions.Append(definition).ToArray();
        if (GetPayloadBytes(candidate) > MaxInlineToolsPayloadBytes)
        {
            _notes.Add(
                $"Tool '{tool.Name}' was omitted from the inline list because the 32 KiB tool budget was exceeded.");
            return;
        }

        _inlineTools.Add(tool);
        _inlineDefinitions.Add(definition);
    }

    private void AddInline(IEnumerable<ITool> tools)
    {
        foreach (var tool in tools)
        {
            _inlineTools.Add(tool);
            _inlineDefinitions.Add(CreateDefinition(tool));
        }

        if (GetPayloadBytes(_inlineDefinitions) > MaxInlineToolsPayloadBytes)
        {
            throw new InvalidOperationException("The serialized tool list exceeds the 32 KiB budget.");
        }
    }

    private static JsonObject CreateDefinition(ITool tool) => new()
    {
        ["type"] = "function",
        ["name"] = tool.Name,
        ["description"] = tool.Description,
        ["parameters"] = tool.Parameters.DeepClone()
    };

    private static int GetPayloadBytes(IEnumerable<JsonObject> definitions)
    {
        var json = new JsonArray(definitions.Select(d => (JsonNode)d.DeepClone()).ToArray()).ToJsonString();
        return Encoding.UTF8.GetByteCount(json);
    }
}
