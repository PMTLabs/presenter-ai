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

    string Title => Name;

    /// <summary>Spoken question for a <see cref="RequiresConfirmation"/> tool; null uses the generic question.</summary>
    string? ConfirmationQuestion => null;

    Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default);
}
