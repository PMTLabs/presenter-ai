namespace PresenterAi.Application.Scripts.Revisions;

/// <summary>
/// <c>Training:*</c> settings (plan 010 §4.3). Bound with <c>Validate</c> + <c>ValidateOnStart</c>; reader:
/// <c>ScriptRevisionService</c> (bounds each reviser call).
/// </summary>
public sealed class TrainingOptions
{
    public const string SectionName = "Training";
    public const int DefaultReviserTimeoutSeconds = 60;
    public const int MinReviserTimeoutSeconds = 10;
    public const int MaxReviserTimeoutSeconds = 180;

    public int ReviserTimeoutSeconds { get; set; } = DefaultReviserTimeoutSeconds;

    public TimeSpan ReviserTimeout => TimeSpan.FromSeconds(ReviserTimeoutSeconds);
}
