using System.Text.Json;
using System.Text.Json.Nodes;
using PresenterAi.Application.Tools;

namespace PresenterAi.Application.Presenting.Tools;

public sealed class PreviousSlideTool : ITool
{
    private readonly IPresenter _presenter;

    public PreviousSlideTool(IPresenter presenter)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
    }

    public string Name => "previous_slide";

    public string Description => "Go back to the previous slide.";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
        ["additionalProperties"] = false
    };

    public IReadOnlyList<string> Tags { get; } = ["presenter", "navigation", "previous"];

    public bool Pinned => true;

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var before = _presenter.Snapshot();
        var changed = await _presenter.PrevAsync(cancellationToken).ConfigureAwait(false);
        var after = _presenter.Snapshot();
        if (!changed || after.SlideIndex == before.SlideIndex)
        {
            return ToolResult.Success($"already on slide {after.SlideIndex + 1} of {after.SlideCount}");
        }

        return ToolResult.Success($"moved to slide {after.SlideIndex + 1} of {after.SlideCount}");
    }
}
