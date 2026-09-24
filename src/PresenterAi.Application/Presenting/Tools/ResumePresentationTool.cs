using System.Text.Json;
using System.Text.Json.Nodes;
using PresenterAi.Application.Tools;

namespace PresenterAi.Application.Presenting.Tools;

public sealed class ResumePresentationTool : ITool
{
    private readonly IPresenter _presenter;

    public ResumePresentationTool(IPresenter presenter)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
    }

    public string Name => "resume_presentation";

    public string Description => "Resume the presentation narration.";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
        ["additionalProperties"] = false
    };

    public IReadOnlyList<string> Tags { get; } = ["presenter", "control", "resume"];

    public bool Pinned => true;

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var snapshot = _presenter.Snapshot();
        var alreadyPresenting = !snapshot.Paused && snapshot.State.Equals("presenting", StringComparison.OrdinalIgnoreCase);
        var changed = await _presenter.ResumeAsync(cancellationToken).ConfigureAwait(false);
        var after = _presenter.Snapshot();
        if (changed || !after.Paused)
        {
            return ToolResult.Success(alreadyPresenting && !changed
                ? $"already presenting on slide {after.SlideIndex + 1} of {after.SlideCount}"
                : $"resumed on slide {after.SlideIndex + 1} of {after.SlideCount}");
        }

        return ToolResult.Failure($"failed to resume on slide {after.SlideIndex + 1} of {after.SlideCount}");
    }
}
