using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PresenterAi.Application.Content;
using PresenterAi.Infrastructure.Content;
using PresenterAi.Infrastructure.Persistence;
using PresenterAi.Infrastructure.Persistence.Entities;
using PresenterAi.Integration.Tests.Support;
using Xunit;

namespace PresenterAi.Integration.Tests.Cli;

[Collection(IntegrationCollection.Name)]
public sealed class ImportCommandTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Reimporting_updates_instead_of_duplicating()
    {
        var owner = await SeedUserAsync();
        var root = FindRepositoryRoot();
        var configuration = Configuration();
        var firstOutput = new StringWriter();
        var firstExit = await PresenterAi.Cli.Program.RunAsync(
            ["import", Path.Combine(root, "presentations", "*.md"), "--owner", owner.Email, "--content-root", root],
            configuration, firstOutput, new StringWriter(), CancellationToken.None);
        var secondOutput = new StringWriter();
        var secondExit = await PresenterAi.Cli.Program.RunAsync(
            ["import", Path.Combine(root, "presentations", "*.md"), "--owner", owner.Email, "--content-root", root],
            configuration, secondOutput, new StringWriter(), CancellationToken.None);

        firstExit.Should().Be(0);
        firstOutput.ToString().Should().Contain("2 created").And.Contain("2 skipped").And.Contain("0 failed");
        secondExit.Should().Be(0);
        secondOutput.ToString().Should().Contain("2 updated").And.Contain("2 skipped");

        await using var context = CreateContext();
        var rows = await context.Presentations.Where(presentation => presentation.OwnerId == owner.Id).ToArrayAsync();
        rows.Should().HaveCount(2);
        rows.Select(row => row.Id).Should().OnlyHaveUniqueItems();
        rows.Should().OnlyContain(row => row.Version == 2);
        var ricoh = rows.Single(row => row.Slug == "ricoh-delivery-overview");
        ricoh.Context.Should().Be(await File.ReadAllTextAsync(Path.Combine(root, "presentations", "ricoh-context.md")));
    }

    [Fact]
    public async Task An_unknown_owner_exits_non_zero_and_writes_no_rows()
    {
        var root = FindRepositoryRoot();
        var configuration = Configuration();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PresenterAi.Cli.Program.RunAsync(
            ["import", Path.Combine(root, "presentations", "sample.md"), "--owner", "missing-owner@example.test", "--content-root", root],
            configuration, output, error, CancellationToken.None);

        exit.Should().Be(1);
        error.ToString().Should().Contain("Unknown owner");
        output.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task A_disabled_owner_exits_non_zero_and_writes_no_rows()
    {
        var owner = await SeedUserAsync(disabled: true);
        var root = FindRepositoryRoot();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PresenterAi.Cli.Program.RunAsync(
            ["import", Path.Combine(root, "presentations", "sample.md"), "--owner", owner.Email, "--content-root", root],
            Configuration(), output, error, CancellationToken.None);

        exit.Should().Be(1);
        error.ToString().Should().Contain("disabled");
        output.ToString().Should().BeEmpty();
        await using var context = CreateContext();
        (await context.Presentations.CountAsync(presentation => presentation.OwnerId == owner.Id)).Should().Be(0);
    }

    [Fact]
    public async Task A_bad_script_does_not_roll_back_a_valid_script()
    {
        var owner = await SeedUserAsync();
        var root = Path.Combine(Path.GetTempPath(), "presenter-ai-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "presentations"));
        var sourceRoot = FindRepositoryRoot();
        File.Copy(Path.Combine(sourceRoot, "presentations", "sample.md"), Path.Combine(root, "presentations", "valid.md"));
        File.Copy(Path.Combine(sourceRoot, "presentations", "sample-context.md"), Path.Combine(root, "presentations", "sample-context.md"));
        await File.WriteAllTextAsync(Path.Combine(root, "presentations", "bad.md"), "---\ntitle: broken\ndeck: decks/sample/index.html\n---\nno slides");
        try
        {
            var output = new StringWriter();
            var exit = await PresenterAi.Cli.Program.RunAsync(
                ["import", Path.Combine(root, "presentations", "*.md"), "--owner", owner.Email, "--content-root", root],
                Configuration(), output, new StringWriter(), CancellationToken.None);

            exit.Should().Be(1);
            output.ToString().Should().Contain("valid.md").And.Contain("created").And.Contain("bad.md").And.Contain("failed");
            await using var context = CreateContext();
            (await context.Presentations.CountAsync(presentation => presentation.OwnerId == owner.Id)).Should().Be(1);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Imported_content_loads_through_the_presentation_repository()
    {
        var owner = await SeedUserAsync();
        var root = FindRepositoryRoot();
        var exit = await PresenterAi.Cli.Program.RunAsync(
            ["import", Path.Combine(root, "presentations", "ricoh-delivery-overview.md"), "--owner", owner.Email, "--content-root", root],
            Configuration(), new StringWriter(), new StringWriter(), CancellationToken.None);
        exit.Should().Be(0);

        await using var context = CreateContext();
        var row = await context.Presentations.SingleAsync(presentation => presentation.OwnerId == owner.Id);
        var loaded = await new PostgresPresentationRepository(context).LoadAsync(owner.Id, row.Id);
        var source = await new FilePresentationRepository(root).ReadAsync("ricoh-delivery-overview");
        loaded.Slides.Count.Should().Be(source.Slides.Count);
        loaded.Context.Should().Be(source.Context);
    }

    private async Task<User> SeedUserAsync(bool disabled = false)
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var user = new User
        {
            Email = $"{Guid.NewGuid():N}@import.test",
            DisplayName = "Import owner",
            AuthMethod = "test",
            IsDisabled = disabled,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    private Microsoft.Extensions.Configuration.IConfiguration Configuration() =>
        new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString
            })
            .Build();

    private PresenterAiDbContext CreateContext() => new(new DbContextOptionsBuilder<PresenterAiDbContext>()
        .UseNpgsql(postgres.ConnectionString)
        .Options);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "PresenterAi.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("PresenterAi.slnx was not found above the test assembly.");
    }
}
