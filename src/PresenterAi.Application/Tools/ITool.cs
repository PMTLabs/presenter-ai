using System.Text.Json;
using System.Text.Json.Nodes;

namespace PresenterAi.Application.Tools;

public interface ITool
{
    string Name { get; }

    string Description { get; }

    JsonObject Parameters { get; }

    IReadOnlyList<string> Tags { get; }

    bool Pinned { get; }

    Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default);
}
