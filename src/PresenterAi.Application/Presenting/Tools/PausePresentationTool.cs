using System.Text.Json;
using System.Text.Json.Nodes;
using PresenterAi.Application.Tools;

namespace PresenterAi.Application.Presenting.Tools;

public sealed class PausePresentationTool : ITool
{
    private readonly IPresenter _presenter;

    public PausePresentationTool(IPresenter presenter)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
    }

    public string Name => "pause_presentation";

    public string Description => "Pause the presentation narration.";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
        ["additionalProperties"] = false
    };

    public IReadOnlyList<string> Tags { get; } = ["presenter", "control", "pause"];

    public bool Pinned => true;

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var snapshot = _presenter.Snapshot();
        if (snapshot.Paused || snapshot.State.Equals("paused", StringComparison.OrdinalIgnoreCase))
        {
            return ToolResult.Success($"already paused on slide {snapshot.SlideIndex + 1} of {snapshot.SlideCount}");
        }

        var changed = await _presenter.PauseAsync(cancellationToken).ConfigureAwait(false);
        var after = _presenter.Snapshot();
        if (changed || after.Paused)
        {
            return ToolResult.Success($"paused on slide {after.SlideIndex + 1} of {after.SlideCount}");
        }

        return ToolResult.Failure($"failed to pause on slide {after.SlideIndex + 1} of {after.SlideCount}");
    }
}
