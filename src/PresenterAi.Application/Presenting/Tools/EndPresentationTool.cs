using System.Text.Json;
using System.Text.Json.Nodes;
using PresenterAi.Application.Tools;

namespace PresenterAi.Application.Presenting.Tools;

public sealed class EndPresentationTool : ITool
{
    private readonly IPresenter _presenter;

    public EndPresentationTool(IPresenter presenter)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
    }

    public string Name => "end_presentation";

    public string Description => "Ask to end the presentation. The talk ends only after the audience confirms.";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["confirmed"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Set to true only if the user has confirmed they want to end the presentation"
            }
        },
        ["required"] = new JsonArray { "confirmed" },
        ["additionalProperties"] = false
    };

    public IReadOnlyList<string> Tags { get; } = ["presenter", "control", "end"];

    public bool Pinned => true;

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("confirmed", out var confirmedElement) ||
            (confirmedElement.ValueKind != JsonValueKind.True && confirmedElement.ValueKind != JsonValueKind.False))
        {
            return ToolResult.Failure("Missing or invalid 'confirmed' parameter.");
        }

        // Plan 007 §3.4: the model can never end the talk by itself. Both values start the end confirmation;
        // ending happens only when the audience answers "yes" to it (the awaiting-confirmation phase, Task 5).
        await _presenter.PauseAsync(cancellationToken).ConfigureAwait(false);
        var after = _presenter.Snapshot();
        return ToolResult.Success(
            $"confirmation required to end presentation: ask the audience to confirm; paused on slide {after.SlideIndex + 1} of {after.SlideCount}");
    }
}
