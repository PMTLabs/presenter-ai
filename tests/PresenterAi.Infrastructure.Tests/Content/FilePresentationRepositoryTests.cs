using FluentAssertions;
using PresenterAi.Infrastructure.Content;

namespace PresenterAi.Infrastructure.Tests.Content;

public sealed class FilePresentationRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "presenter-ai-tests", Guid.NewGuid().ToString("N"));

    public FilePresentationRepositoryTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "presentations"));
        File.WriteAllText(Path.Combine(_root, "presentations", "with-context.md"), """
            ---
            title: With context
            deck: decks/sample/index.html
            context: presentations/with-context-context.md
            ---
            ## Slide 1: One
            Hello.
            """);
        File.WriteAllText(Path.Combine(_root, "presentations", "with-context-context.md"), "Background facts.");
        File.WriteAllText(Path.Combine(_root, "presentations", "escaping.md"), """
            ---
            title: Escaping
            deck: decks/sample/index.html
            context: ../outside.md
            ---
            ## Slide 1: One
            Hello.
            """);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_root)!, "outside.md"), "must not be readable");
    }

    [Theory]
    [InlineData("none")]
    [InlineData("primary")]
    [InlineData("alt")]
    public async Task Loads_context_whether_or_not_the_root_has_a_trailing_separator(string separator)
    {
        // The real appsettings root is "../../" → Path.GetFullPath keeps the trailing separator; the first
        // real run failed every context load with "context path escapes the project". Both separators the OS
        // recognises are covered ('' and '/' on Windows, '/' twice on Linux — a literal '' is a file name there).
        var suffix = separator switch
        {
            "primary" => Path.DirectorySeparatorChar.ToString(),
            "alt" => Path.AltDirectorySeparatorChar.ToString(),
            _ => string.Empty
        };
        var repository = new FilePresentationRepository(_root + suffix);

        var loaded = await repository.ReadAsync("with-context");

        loaded.Context.Should().Be("Background facts.");
        loaded.Slides.Should().HaveCount(1);

        var source = await repository.ReadSourceAsync("with-context");
        source.Markdown.Should().Contain("context: presentations/with-context-context.md");
        source.Presentation.Context.Should().Be(loaded.Context);
    }

    [Fact]
    public async Task Rejects_a_context_path_outside_the_root()
    {
        var repository = new FilePresentationRepository(_root + Path.DirectorySeparatorChar);

        var load = () => repository.ReadAsync("escaping");
        var sourceLoad = () => repository.ReadSourceAsync("escaping");

        await load.Should().ThrowAsync<ArgumentException>().WithMessage("*escapes the project*");
        await sourceLoad.Should().ThrowAsync<ArgumentException>().WithMessage("*escapes the project*");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
