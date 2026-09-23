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

    bool RequiresConfirmation => false;

    TimeSpan Timeout => TimeSpan.FromSeconds(5);

    string Source => "presenter";

    Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default);
}
