namespace PresenterAi.Application.Scripts.Revisions;

/// <summary>The <c>presentation_revisions.source</c> values (plan 010 §4.3, CHECK constraint).</summary>
public static class RevisionSources
{
    public const string Import = "import";
    public const string LiveEdit = "live_edit";
    public const string Revert = "revert";

    public static IReadOnlyList<string> All { get; } = [Import, LiveEdit, Revert];

    public static bool IsKnown(string? source) => source is Import or LiveEdit or Revert;
}
