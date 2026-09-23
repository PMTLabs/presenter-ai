using System.Text.Json;
using System.Text.Json.Nodes;
using PresenterAi.Application.Tools;

namespace PresenterAi.Application.Presenting.Tools;

public sealed class GoToSlideTool : ITool
{
    private readonly IPresenter _presenter;

    public GoToSlideTool(IPresenter presenter)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
    }

    public string Name => "go_to_slide";

    public string Description => "Navigate directly to a specific slide number (1-based).";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["slide_number"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "1-based slide number to navigate to"
            }
        },
        ["required"] = new JsonArray { "slide_number" },
        ["additionalProperties"] = false
    };

    public IReadOnlyList<string> Tags { get; } = ["presenter", "navigation", "goto"];

    public bool Pinned => true;

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("slide_number", out var slideNumElement) || !slideNumElement.TryGetInt32(out var slideNumber))
        {
            return ToolResult.Failure("Missing or invalid 'slide_number' parameter.");
        }

        var snapshot = _presenter.Snapshot();
        if (slideNumber < 1 || slideNumber > snapshot.SlideCount)
        {
            return ToolResult.Failure($"there are slides 1 to {snapshot.SlideCount}");
        }

        if (slideNumber == snapshot.SlideIndex + 1)
        {
            return ToolResult.Success($"already on slide {slideNumber} of {snapshot.SlideCount}");
        }

        var changed = await _presenter.GotoAsync(slideNumber - 1, cancellationToken).ConfigureAwait(false);
        var after = _presenter.Snapshot();
        if (changed || after.SlideIndex + 1 == slideNumber)
        {
            return ToolResult.Success($"moved to slide {after.SlideIndex + 1} of {after.SlideCount}");
        }

        return ToolResult.Failure($"failed to navigate to slide {slideNumber} of {snapshot.SlideCount}");
    }
}
