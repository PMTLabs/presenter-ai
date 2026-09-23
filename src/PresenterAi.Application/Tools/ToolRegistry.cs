using System.Text;
using System.Text.RegularExpressions;

namespace PresenterAi.Application.Tools;

public sealed partial class ToolRegistry
{
    public const int MaxPinnedTools = 12;
    public const int MaxDescriptionLength = 1024;
    public const int MaxParametersBytes = 4096;

    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private int _pinnedCount;

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex ToolNameRegex();

    public int Count => _tools.Count;

    public int PinnedCount => _pinnedCount;

    public bool Contains(string name) => _tools.ContainsKey(name);

    public void Register(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (string.IsNullOrWhiteSpace(tool.Name) || !ToolNameRegex().IsMatch(tool.Name))
        {
            throw new ArgumentException(
                $"Tool name '{tool.Name}' is invalid. Must match '^[a-zA-Z0-9_-]{{1,64}}$'.",
                nameof(tool));
        }

        if (_tools.ContainsKey(tool.Name))
        {
            throw new InvalidOperationException($"Tool with name '{tool.Name}' is already registered.");
        }

        if (tool.Description is { Length: > MaxDescriptionLength })
        {
            throw new ArgumentException(
                $"Tool description exceeds maximum length of {MaxDescriptionLength} characters ({tool.Description.Length}).",
                nameof(tool));
        }

        ArgumentNullException.ThrowIfNull(tool.Parameters);
        var paramsJson = tool.Parameters.ToJsonString();
        var paramsBytes = Encoding.UTF8.GetByteCount(paramsJson);
        if (paramsBytes > MaxParametersBytes)
        {
            throw new ArgumentException(
                $"Tool parameters schema exceeds {MaxParametersBytes} bytes ({paramsBytes} bytes).",
                nameof(tool));
        }

        var rootType = tool.Parameters["type"]?.GetValue<string>();
        if (rootType != "object")
        {
            throw new ArgumentException(
                $"Tool parameters schema root type must be 'object', but was '{rootType}'.",
                nameof(tool));
        }

        if (tool.Pinned)
        {
            if (_pinnedCount >= MaxPinnedTools)
            {
                throw new InvalidOperationException(
                    $"Cannot register more than {MaxPinnedTools} pinned tools.");
            }

            _pinnedCount++;
        }

        _tools[tool.Name] = tool;
    }

    public IReadOnlyList<ITool> GetAllTools()
    {
        return _tools.Values.ToList();
    }

    public ToolSessionCatalogue CreateCatalogue(int maxInlineTools = ToolsOptions.DefaultMaxInlineTools)
    {
        return new ToolSessionCatalogue(GetAllTools(), maxInlineTools);
    }
}
