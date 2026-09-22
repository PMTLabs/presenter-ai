using System.Text.RegularExpressions;
using PresenterAi.Application.Content;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Scripts;

namespace PresenterAi.Infrastructure.Content;

public sealed partial class FilePresentationRepository(string rootDir) : IPresentationImportSource
{
    // Trailing separators are trimmed so the IsWithinRoot prefix check works for "../../"-style roots
    // (Path.GetFullPath keeps the trailing separator, which broke every context load in a real run).
    private readonly string _rootDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDir));

    public async Task<IReadOnlyList<PresentationListRow>> ListFilesAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(_rootDir, "presentations");
        var rows = new List<PresentationListRow>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                     .Where(path => !path.EndsWith("-context.md", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(file);
            try
            {
                var script = ScriptParser.Parse(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false), id);
                rows.Add(new PresentationListRow(id, script.Meta.Title, script.Slides.Count, script.Meta.Deck, script.Meta.Driver, null));
            }
            catch (Exception exception)
            {
                rows.Add(new PresentationListRow(id, id, null, null, null, exception.Message));
            }
        }

        return rows.OrderBy(row => row.Id, StringComparer.Ordinal).ToArray();
    }

    public async Task<LoadedPresentation> ReadAsync(string id, CancellationToken cancellationToken = default) =>
        (await ReadSourceAsync(id, cancellationToken).ConfigureAwait(false)).Presentation;

    public async Task<PresentationSource> ReadSourceAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !IdRegex().IsMatch(id))
        {
            throw new ArgumentException($"invalid presentation id \"{id}\"", nameof(id));
        }

        var file = Path.Combine(_rootDir, "presentations", $"{id}.md");
        var markdown = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        var script = ScriptParser.Parse(markdown, id);
        string? context = null;
        if (!string.IsNullOrEmpty(script.Meta.Context))
        {
            var contextPath = Path.GetFullPath(script.Meta.Context, _rootDir);
            if (!IsWithinRoot(contextPath))
            {
                throw new ArgumentException($"context path escapes the project: {script.Meta.Context}");
            }

            context = await File.ReadAllTextAsync(contextPath, cancellationToken).ConfigureAwait(false);
        }

        return new PresentationSource(
            markdown,
            new LoadedPresentation(script.Meta.Id, script.Meta, script.Slides, context));
    }

    private bool IsWithinRoot(string path) => path.StartsWith(_rootDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, _rootDir, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[\\w.-]+$")]
    private static partial Regex IdRegex();
}
