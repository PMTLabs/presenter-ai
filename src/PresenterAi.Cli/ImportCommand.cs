using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Application.Content;
using PresenterAi.Infrastructure.Persistence;
using PresenterAi.Infrastructure.Persistence.Entities;

namespace PresenterAi.Cli;

public static class ImportCommand
{
    public static async Task<int> RunAsync(
        ImportArguments arguments,
        IConfiguration configuration,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var contentRoot = arguments.ContentRoot is null
            ? configuration["Content:RootDir"] is { Length: > 0 } configuredRoot
                ? Path.GetFullPath(configuredRoot, Directory.GetCurrentDirectory())
                : FindRepositoryRoot(Directory.GetCurrentDirectory())
            : Path.GetFullPath(arguments.ContentRoot, Directory.GetCurrentDirectory());

        await using var services = Program.BuildImportServices(configuration, contentRoot);
        var source = services.GetRequiredService<IPresentationImportSource>();
        var timeProvider = services.GetRequiredService<TimeProvider>();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PresenterAiDbContext>();

        var ownerResult = await OwnerResolver.ResolveAsync(db, arguments.Owner, cancellationToken).ConfigureAwait(false);
        if (ownerResult.User is null)
        {
            await error.WriteLineAsync(ownerResult.Error!).ConfigureAwait(false);
            return 1;
        }

        var owner = ownerResult.User;
        var expanded = Expand(arguments.Paths, Directory.GetCurrentDirectory());
        var created = 0;
        var updated = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var item in expanded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Error is not null)
            {
                await output.WriteLineAsync($"{item.Display}: failed: {item.Error}").ConfigureAwait(false);
                failed++;
                continue;
            }

            var path = item.Path!;
            if (!IsDirectPresentationFile(path, contentRoot))
            {
                await output.WriteLineAsync($"{path}: failed: file must be a .md directly inside {Path.Combine(contentRoot, "presentations")}").ConfigureAwait(false);
                failed++;
                continue;
            }

            if (path.EndsWith("-context.md", StringComparison.OrdinalIgnoreCase))
            {
                await output.WriteLineAsync($"{path}: skipped").ConfigureAwait(false);
                skipped++;
                continue;
            }

            var slug = Path.GetFileNameWithoutExtension(path);
            try
            {
                var imported = await source.ReadSourceAsync(slug, cancellationToken).ConfigureAwait(false);
                var presentation = await db.Presentations.SingleOrDefaultAsync(
                    candidate => candidate.OwnerId == owner.Id && candidate.Slug == slug,
                    cancellationToken).ConfigureAwait(false);
                var now = timeProvider.GetUtcNow();
                var isNew = presentation is null;
                presentation ??= new Presentation
                {
                    OwnerId = owner.Id,
                    Slug = slug,
                    CreatedAt = now,
                    Version = 1
                };

                presentation.Title = imported.Presentation.Meta.Title;
                presentation.Deck = imported.Presentation.Meta.Deck;
                presentation.Driver = imported.Presentation.Meta.Driver;
                presentation.Script = imported.Markdown;
                presentation.Context = imported.Presentation.Context;
                presentation.Frontmatter = CreateFrontmatter(imported.Presentation.Meta);
                presentation.SlideCount = imported.Presentation.Slides.Count;
                presentation.UpdatedAt = now;
                if (isNew)
                {
                    db.Presentations.Add(presentation);
                }
                else
                {
                    presentation.Version++;
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"{path}: {(isNew ? "created" : "updated")}").ConfigureAwait(false);
                if (isNew) created++; else updated++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                await output.WriteLineAsync($"{path}: failed: {exception.Message}").ConfigureAwait(false);
                failed++;
            }
        }

        await output.WriteLineAsync($"Summary: {created} created, {updated} updated, {skipped} skipped, {failed} failed").ConfigureAwait(false);
        return failed == 0 ? 0 : 1;
    }

    private static JsonDocument CreateFrontmatter(PresenterAi.Application.Scripts.PresentationMeta meta) =>
        JsonSerializer.SerializeToDocument(new
        {
            id = meta.Id,
            title = meta.Title,
            deck = meta.Deck,
            driver = meta.Driver,
            voice = meta.Voice,
            context = meta.Context,
            advanceSilenceMs = meta.AdvanceSilenceMs,
            chunkChars = meta.ChunkChars
        });

    private static bool IsDirectPresentationFile(string path, string contentRoot)
    {
        var presentations = Path.GetFullPath(Path.Combine(contentRoot, "presentations"));
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        return string.Equals(directory, presentations, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase)
            && File.Exists(path);
    }

    private static IReadOnlyList<ResolvedFile> Expand(IReadOnlyList<string> arguments, string currentDirectory)
    {
        var results = new List<ResolvedFile>();
        foreach (var argument in arguments)
        {
            var full = Path.GetFullPath(argument, currentDirectory);
            if (!HasWildcard(full))
            {
                results.Add(new ResolvedFile(full, null, full));
                continue;
            }

            var directory = Path.GetDirectoryName(full) ?? currentDirectory;
            var pattern = Path.GetFileName(full);
            if (!Directory.Exists(directory))
            {
                results.Add(new ResolvedFile(null, $"no files matched {argument}", argument));
                continue;
            }

            var matches = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
            if (matches.Length == 0)
            {
                results.Add(new ResolvedFile(null, $"no files matched {argument}", argument));
                continue;
            }

            foreach (var match in matches.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(new ResolvedFile(Path.GetFullPath(match), null, match));
            }
        }

        return results
            .GroupBy(item => item.Path ?? $"error:{item.Display}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static bool HasWildcard(string path) => path.IndexOfAny(['*', '?', '[']) >= 0;

    private static string FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PresenterAi.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find PresenterAi.slnx above the current directory.");
    }

    private sealed record ResolvedFile(string? Path, string? Error, string Display);
}
