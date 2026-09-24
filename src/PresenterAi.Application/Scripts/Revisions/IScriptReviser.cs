namespace PresenterAi.Application.Scripts.Revisions;

/// <summary>
/// Out-of-band reasoning call that rewrites the narration of the target slides (plan 010 §4.1). Implemented by
/// <c>ResponsesScriptReviser</c> (Infrastructure). Never throws for upstream, timeout, cancellation or output problems:
/// those come back as <see cref="ScriptRevisionResult.Failed"/>.
/// </summary>
public interface IScriptReviser
{
    /// <summary>True when at least one upstream route has a <c>DelegationModel</c> ("transcript training" available).</summary>
    bool IsAvailable => true;

    Task<ScriptRevisionResult> ReviseAsync(ScriptRevisionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The reviser input, serialised as the <c>input</c> JSON object
/// <c>{title, outline:[{number,title}], targets:[{number,title,narration,notes}], request:{feedback, exchange?:{question,answer}}, context:{recent:[{role,text}]}}</c>.
/// Only <see cref="Feedback"/> and <see cref="Exchange"/> carry editing intent; <see cref="Recent"/> is untrusted context.
/// </summary>
public sealed record ScriptRevisionRequest(
    string Title,
    IReadOnlyList<ScriptOutlineEntry> Outline,
    IReadOnlyList<ScriptRevisionTarget> Targets,
    string Feedback,
    TrainingExchange? Exchange,
    IReadOnlyList<RecentTurn> Recent);

public sealed record ScriptOutlineEntry(int Number, string Title);

public sealed record ScriptRevisionTarget(int Number, string Title, string Narration, string? Notes);

/// <summary>A question and its answer picked with "Train on this".</summary>
public sealed record TrainingExchange(string Question, string Answer);

/// <summary>A recent transcript turn (role <c>user</c> or <c>assistant</c>), context only.</summary>
public sealed record RecentTurn(string Role, string Text);

/// <summary>New narration for one slide, by 1-based slide number.</summary>
public sealed record RevisedSlide(int Number, string Narration);

public abstract record ScriptRevisionResult
{
    private ScriptRevisionResult()
    {
    }

    public sealed record Ok(IReadOnlyList<RevisedSlide> Slides, string Summary) : ScriptRevisionResult;

    /// <param name="Reason">
    /// <see cref="ScriptEditErrors.Timeout"/>, <see cref="ScriptEditErrors.Upstream"/>,
    /// <see cref="ScriptEditErrors.InvalidOutput"/> or <see cref="ScriptEditErrors.Cancelled"/>.
    /// </param>
    public sealed record Failed(string Reason) : ScriptRevisionResult;
}
