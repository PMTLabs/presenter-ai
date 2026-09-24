namespace PresenterAi.Infrastructure.Tests.Content;

/// <summary>
/// A content root holding only the sample and Ricoh presentations and decks, copied from the repository. Tests that
/// list or import every presentation use it so adding a new deck under presentations/ does not change their oracle.
/// </summary>
public static class PinnedContentRoot
{
    public static readonly IReadOnlyList<string> Files =
    [
        "presentations/sample.md",
        "presentations/sample-context.md",
        "presentations/ricoh-delivery-overview.md",
        "presentations/ricoh-context.md",
        "decks/sample/index.html",
        "decks/ricoh/index.html"
    ];

    public static string Create(string repositoryRoot)
    {
        var root = Path.Combine(Path.GetTempPath(), "presenter-ai-pinned-content", Guid.NewGuid().ToString("N"));
        foreach (var file in Files)
        {
            var target = Path.Combine(root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(repositoryRoot, file), target);
        }

        return root;
    }
}
