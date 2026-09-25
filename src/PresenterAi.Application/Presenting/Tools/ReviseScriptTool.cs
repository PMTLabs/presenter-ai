using System.Text.Json;
using System.Text.Json.Nodes;
using PresenterAi.Application.Tools;

namespace PresenterAi.Application.Presenting.Tools;

/// <summary>
/// Plan 010 <c>revise_script</c>: declared in every managed talk and gated by the presenter on the resolved name (Trainer
/// mode off → <c>trainer_mode_off</c>, no confirmation). The presenter captures the edit intent when it asks the
/// confirmation question and enqueues it on the loop at "yes"; this tool has no side effect and only returns the spoken
/// acknowledgement. <c>additionalProperties:false</c> keeps the model from adding any other field.
/// </summary>
public sealed class ReviseScriptTool : ITool
{
    public const string ToolName = "revise_script";
    public const string Question = "Shall I add that to the script?";
    public const string Acknowledgement = "Got it — updating the script.";

    public string Name => ToolName;

    public string Description =>
        "Change the presentation script when the speaker asks for it (add, correct or remove something said on a slide). " +
        "Pass their request in their own words. Only the slides named, or the current slide, are rewritten.";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["feedback"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The requested change in the speaker's words (1 to 2000 characters)"
            },
            ["slide_numbers"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "integer" },
                ["description"] = "1-based slide numbers to change; omit for the current slide"
            }
        },
        ["required"] = new JsonArray { "feedback" },
        ["additionalProperties"] = false
    };

    public IReadOnlyList<string> Tags { get; } = ["presenter", "script", "training"];

    public bool Pinned => true;

    public bool RequiresConfirmation => true;

    public string? ConfirmationQuestion => Question;

    public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        Task.FromResult(ToolResult.Success(Acknowledgement));
}
