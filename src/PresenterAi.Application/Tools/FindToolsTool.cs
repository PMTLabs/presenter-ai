using System.Text.Json;
using System.Text.Json.Nodes;

namespace PresenterAi.Application.Tools;

public sealed class FindToolsTool : ITool
{
    private static readonly char[] Separators = [' ', '\t', '\r', '\n', '_', '-', '.', ',', ':', ';', '!', '?', '(', ')', '[', ']', '{', '}', '/', '\\', '"', '\''];

    private readonly Func<IReadOnlyList<ITool>> _toolsProvider;

    public FindToolsTool(Func<IReadOnlyList<ITool>> toolsProvider)
    {
        _toolsProvider = toolsProvider ?? throw new ArgumentNullException(nameof(toolsProvider));
    }

    public string Name => "find_tools";

    public string Description => "Search available tools by keyword query.";

    public IReadOnlyList<string> Tags => ["search", "tools", "find"];

    public bool Pinned => true;

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The search query keywords to find tools."
            }
        },
        ["required"] = new JsonArray { "query" },
        ["additionalProperties"] = false
    };

    public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var query = string.Empty;
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("query", out var queryProp))
        {
            query = queryProp.GetString() ?? string.Empty;
        }

        var results = RankTools(query, _toolsProvider());
        var matches = new JsonArray();

        foreach (var tool in results)
        {
            matches.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = tool.Parameters.DeepClone()
            });
        }

        var message = matches.Count > 0
            ? $"Found {matches.Count} tool(s) matching '{query}'."
            : $"No tools found matching '{query}'.";

        return Task.FromResult(ToolResult.Success(message, matches));
    }

    internal static IReadOnlyList<ITool> RankTools(string query, IReadOnlyList<ITool> tools)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var queryTerms = Tokenize(query).Distinct().ToList();
        if (queryTerms.Count == 0)
        {
            return [];
        }

        var scored = new List<(ITool Tool, int Score)>();

        foreach (var tool in tools)
        {
            // Do not include meta-tools in search results
            if (tool.Name is "find_tools" or "call_tool")
            {
                continue;
            }

            var nameTokens = Tokenize(tool.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var tagTokens = tool.Tags.SelectMany(Tokenize).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var descTokens = Tokenize(tool.Description).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var score = 0;
            foreach (var term in queryTerms)
            {
                // Score weights per plan §3.1: name x3, tags x2, description x1
                if (nameTokens.Contains(term) || tool.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    score += 3;
                }

                if (tagTokens.Contains(term))
                {
                    score += 2;
                }

                if (descTokens.Contains(term))
                {
                    score += 1;
                }
            }

            if (score > 0)
            {
                scored.Add((tool, score));
            }
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Tool.Name, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(s => s.Tool)
            .ToList();
    }

    internal static IEnumerable<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var parts = text.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var stripped = StripPlural(part.Trim());
            if (!string.IsNullOrEmpty(stripped))
            {
                yield return stripped;
            }
        }
    }

    internal static string StripPlural(string word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return word;
        }

        var w = word.ToLowerInvariant();
        if (w.EndsWith("ies", StringComparison.Ordinal) && w.Length > 4)
        {
            return w[..^3] + "y";
        }

        if (w.EndsWith("es", StringComparison.Ordinal) && w.Length > 3)
        {
            return w[..^2];
        }

        if (w.EndsWith("s", StringComparison.Ordinal) && !w.EndsWith("ss", StringComparison.Ordinal) && w.Length > 2)
        {
            return w[..^1];
        }

        return w;
    }
}
