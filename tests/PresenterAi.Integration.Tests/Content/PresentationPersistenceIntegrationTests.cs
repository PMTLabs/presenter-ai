using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PresenterAi.Infrastructure.Content;
using PresenterAi.Infrastructure.Persistence;
using PresenterAi.Infrastructure.Persistence.Entities;
using PresenterAi.Integration.Tests.Support;
using Xunit;

namespace PresenterAi.Integration.Tests.Content;

[Collection(IntegrationCollection.Name)]
public sealed class PresentationPersistenceIntegrationTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task Loaded_presentation_carries_its_context_text()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();

        var owner = NewUser();
        var scriptPath = Path.Combine(FindRepositoryRoot(), "presentations", "ricoh-delivery-overview.md");
        var contextPath = Path.Combine(FindRepositoryRoot(), "presentations", "ricoh-context.md");
        var script = await File.ReadAllTextAsync(scriptPath);
        var contextText = await File.ReadAllTextAsync(contextPath);
        var parsed = PresenterAi.Application.Scripts.ScriptParser.Parse(script, "prs_context_fixture");
        var presentation = NewPresentation(owner, script, contextText, parsed.Slides.Count);
        context.Users.Add(owner);
        context.Presentations.Add(presentation);
        await context.SaveChangesAsync();

        await using var readContext = CreateContext();
        var loaded = await new PostgresPresentationRepository(readContext)
            .LoadAsync(owner.Id, presentation.Id);

        loaded.Context.Should().Be(contextText);
    }

    [Fact]
    public async Task Owner_scoped_listing_excludes_other_users_rows()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();

        var ownerA = NewUser();
        var ownerB = NewUser();
        var script = await File.ReadAllTextAsync(Path.Combine(FindRepositoryRoot(), "presentations", "sample.md"));
        var parsed = PresenterAi.Application.Scripts.ScriptParser.Parse(script, "fixture");
        context.Users.AddRange(ownerA, ownerB);
        context.Presentations.AddRange(
            NewPresentation(ownerA, script, null, parsed.Slides.Count),
            NewPresentation(ownerB, script, null, parsed.Slides.Count));
        await context.SaveChangesAsync();

        await using var readContext = CreateContext();
        var result = await new PostgresPresentationRepository(readContext).ListAsync(ownerA.Id, 1, 25);

        result.Items.Should().OnlyContain(row => row.Id == context.Presentations.Local.Single(p => p.OwnerId == ownerA.Id).Id);
        result.Total.Should().Be(1);
    }

    [Fact]
    public async Task Paging_returns_page_two_and_preserves_total_for_out_of_range_pages()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();

        var owner = NewUser();
        var script = await File.ReadAllTextAsync(Path.Combine(FindRepositoryRoot(), "presentations", "sample.md"));
        var parsed = PresenterAi.Application.Scripts.ScriptParser.Parse(script, "fixture");
        var presentations = Enumerable.Range(1, 26)
            .Select(index => NewPresentation(owner, script, null, parsed.Slides.Count, $"paging-{index:00}"))
            .ToArray();
        context.Users.Add(owner);
        context.Presentations.AddRange(presentations);
        await context.SaveChangesAsync();

        await using var factory = new IntegrationApiFactory(postgres, redis);
        using var client = factory.CreateAuthenticatedClient(owner.Id);

        var pageTwo = await client.GetAsync("/v1/presentations?page=2&pageSize=25");
        using var pageTwoJson = JsonDocument.Parse(await pageTwo.Content.ReadAsStringAsync());
        pageTwoJson.RootElement.GetProperty("items").GetArrayLength().Should().Be(1);
        pageTwoJson.RootElement.GetProperty("items")[0].GetProperty("id").GetString()
            .Should().Be(presentations[^1].Id);
        pageTwoJson.RootElement.GetProperty("total").GetInt32().Should().Be(26);

        var outOfRange = await client.GetAsync("/v1/presentations?page=3&pageSize=25");
        using var outOfRangeJson = JsonDocument.Parse(await outOfRange.Content.ReadAsStringAsync());
        outOfRangeJson.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
        outOfRangeJson.RootElement.GetProperty("total").GetInt32().Should().Be(26);

        var invalidPageSize = await client.GetAsync("/v1/presentations?pageSize=101");
        invalidPageSize.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        (await invalidPageSize.Content.ReadAsStringAsync()).Should().Contain("validation.failed");
    }

    [Fact]
    public async Task Another_users_presentation_is_not_found()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();

        var ownerA = NewUser();
        var ownerB = NewUser();
        var script = await File.ReadAllTextAsync(Path.Combine(FindRepositoryRoot(), "presentations", "sample.md"));
        var parsed = PresenterAi.Application.Scripts.ScriptParser.Parse(script, "fixture");
        var presentation = NewPresentation(ownerA, script, null, parsed.Slides.Count);
        context.Users.AddRange(ownerA, ownerB);
        context.Presentations.Add(presentation);
        await context.SaveChangesAsync();

        await using var factory = new IntegrationApiFactory(postgres, redis);
        using var client = factory.CreateAuthenticatedClient(ownerB.Id);
        var response = await client.GetAsync($"/v1/presentations/{presentation.Id}");
        var body = await response.Content.ReadAsStringAsync();
        var missingResponse = await client.GetAsync("/v1/presentations/prs_missing_for_indistinguishability");
        var missingBody = await missingResponse.Content.ReadAsStringAsync();
        using var otherUserJson = JsonDocument.Parse(body);
        using var missingJson = JsonDocument.Parse(missingBody);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        missingResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        foreach (var property in new[] { "status", "code", "title", "detail" })
        {
            otherUserJson.RootElement.GetProperty(property).GetRawText()
                .Should().Be(missingJson.RootElement.GetProperty(property).GetRawText());
        }
    }

    private PresenterAiDbContext CreateContext() => new(new DbContextOptionsBuilder<PresenterAiDbContext>()
        .UseNpgsql(postgres.ConnectionString)
        .Options);

    private static User NewUser() => new()
    {
        Email = $"{Guid.NewGuid():N}@example.test",
        DisplayName = "Persistence test",
        AuthMethod = "test",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static Presentation NewPresentation(
        User owner,
        string script,
        string? context,
        int slideCount,
        string? slug = null) => new()
    {
        OwnerId = owner.Id,
        Slug = slug ?? Guid.NewGuid().ToString("N"),
        Title = "Persistence fixture",
        Deck = "decks/sample/index.html",
        Driver = "auto",
        Script = script,
        Context = context,
        SlideCount = slideCount,
        Version = 1,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "PresenterAi.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("PresenterAi.slnx was not found above the test assembly.");
    }
}
