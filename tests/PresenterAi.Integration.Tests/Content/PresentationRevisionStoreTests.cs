using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using PresenterAi.Application.Scripts.Revisions;
using PresenterAi.Infrastructure.Content;
using PresenterAi.Infrastructure.Persistence;
using PresenterAi.Infrastructure.Persistence.Entities;
using PresenterAi.Infrastructure.Persistence.Migrations;
using PresenterAi.Integration.Tests.Support;
using Xunit;

namespace PresenterAi.Integration.Tests.Content;

/// <summary>Plan 010 T2: the Postgres revision store and the <c>PresentationRevisions</c> migration.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class PresentationRevisionStoreTests(PostgresFixture postgres)
{
    private const string MigrationBefore = "20260923231440_SessionBillingGuards";
    private const string OriginalSentence = "Hello everyone, and welcome.";

    [Fact]
    public async Task Append_advances_version_and_keeps_every_revision()
    {
        var (owner, presentation) = await SeedAsync(postgres.ConnectionString);
        var v1 = presentation.Script;
        var v2 = v1.Replace(OriginalSentence, "Hello everyone, and welcome to version two.", StringComparison.Ordinal);
        var v3 = v1.Replace(OriginalSentence, "Hello everyone, and welcome to version three.", StringComparison.Ordinal);

        var first = await AppendAsync(owner.Id, presentation.Id, 1,
            new NewRevision(v2, RevisionSources.LiveEdit, "Welcome says version two", 1, null, [0], owner.Id));
        var second = await AppendAsync(owner.Id, presentation.Id, 2,
            new NewRevision(v1, RevisionSources.Revert, "Reverted to v1", 2, 1, [0], owner.Id));
        var third = await AppendAsync(owner.Id, presentation.Id, 3,
            new NewRevision(v3, RevisionSources.LiveEdit, "Welcome says version three", 3, null, [0, 1], null));

        first.Should().Be(new AppendResult.Applied(2));
        second.Should().Be(new AppendResult.Applied(3));
        third.Should().Be(new AppendResult.Applied(4));

        await using var context = CreateContext(postgres.ConnectionString);
        var head = await context.Presentations.AsNoTracking().SingleAsync(row => row.Id == presentation.Id);
        head.Script.Should().Be(v3);
        head.Version.Should().Be(4);
        var rows = await context.PresentationRevisions.AsNoTracking()
            .Where(row => row.PresentationId == presentation.Id)
            .OrderBy(row => row.Number)
            .ToArrayAsync();
        rows.Select(row => row.Number).Should().Equal(1, 2, 3, 4);
        rows.Select(row => row.Source).Should().Equal(
            RevisionSources.Import, RevisionSources.LiveEdit, RevisionSources.Revert, RevisionSources.LiveEdit);
        rows.Select(row => row.Script).Should().Equal(v1, v2, v1, v3);
        rows.Select(row => row.BaseVersion).Should().Equal(null, 1, 2, 3);
        rows.Select(row => row.RevertedFrom).Should().Equal(null, null, 1, null);
        rows.Select(row => row.Summary).Should().Equal("Imported", "Welcome says version two", "Reverted to v1", "Welcome says version three");
        rows[3].ChangedSlides.Should().Equal(0, 1);
        rows[1].CreatedBy.Should().Be(owner.Id);
        rows[3].CreatedBy.Should().BeNull();

        await using var readContext = CreateContext(postgres.ConnectionString);
        var store = new PostgresPresentationRevisionStore(readContext, TimeProvider.System);
        var storeHead = await store.GetHeadAsync(owner.Id, presentation.Id);
        storeHead!.Version.Should().Be(4);
        storeHead.Markdown.Should().Be(v3);
        storeHead.Slides[0].Narration.Should().Contain("welcome to version three");

        var page = await store.ListAsync(owner.Id, presentation.Id, 1, 3);
        page!.Total.Should().Be(4);
        page.CurrentVersion.Should().Be(4);
        page.Items.Select(item => item.Number).Should().Equal(4, 3, 2);
        (await store.ListAsync(owner.Id, presentation.Id, 2, 3))!.Items.Select(item => item.Number).Should().Equal(1);

        var reverted = await store.GetAsync(owner.Id, presentation.Id, 3);
        reverted!.Script.Should().Be(v1);
        reverted.Info.RevertedFrom.Should().Be(1);
        reverted.Info.Source.Should().Be(RevisionSources.Revert);
        (await store.GetAsync(owner.Id, presentation.Id, 5)).Should().BeNull();

        var loaded = await new PostgresPresentationRepository(readContext).LoadAsync(owner.Id, presentation.Id);
        loaded.Version.Should().Be(4);
    }

    [Fact]
    public async Task Stale_expected_version_conflicts_and_writes_nothing()
    {
        var (owner, presentation) = await SeedAsync(postgres.ConnectionString);
        var v2 = presentation.Script.Replace(OriginalSentence, "Hello, version two.", StringComparison.Ordinal);
        (await AppendAsync(owner.Id, presentation.Id, 1,
            new NewRevision(v2, RevisionSources.LiveEdit, "two", 1, null, [0], owner.Id))).Should().Be(new AppendResult.Applied(2));

        var stale = await AppendAsync(owner.Id, presentation.Id, 1,
            new NewRevision(presentation.Script, RevisionSources.LiveEdit, "stale", 1, null, [0], owner.Id));

        stale.Should().Be(new AppendResult.Conflict(2));
        await using var context = CreateContext(postgres.ConnectionString);
        var head = await context.Presentations.AsNoTracking().SingleAsync(row => row.Id == presentation.Id);
        head.Version.Should().Be(2);
        head.Script.Should().Be(v2);
        (await context.PresentationRevisions.CountAsync(row => row.PresentationId == presentation.Id)).Should().Be(2);
    }

    [Fact]
    public async Task Concurrent_appends_on_the_same_version_apply_exactly_once()
    {
        var (owner, presentation) = await SeedAsync(postgres.ConnectionString);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(index => Task.Run(() => AppendAsync(
            owner.Id, presentation.Id, 1,
            new NewRevision(
                presentation.Script.Replace(OriginalSentence, $"Hello from writer {index}.", StringComparison.Ordinal),
                RevisionSources.LiveEdit, $"writer {index}", 1, null, [0], owner.Id)))));

        results.OfType<AppendResult.Applied>().Should().ContainSingle().Which.Version.Should().Be(2);
        results.OfType<AppendResult.Conflict>().Should().HaveCount(5).And.OnlyContain(conflict => conflict.CurrentVersion == 2);
        await using var context = CreateContext(postgres.ConnectionString);
        (await context.PresentationRevisions.CountAsync(row => row.PresentationId == presentation.Id)).Should().Be(2);
    }

    [Fact]
    public async Task Other_owner_sees_not_found()
    {
        var (owner, presentation) = await SeedAsync(postgres.ConnectionString);
        var stranger = await SeedUserAsync(postgres.ConnectionString);
        await using var context = CreateContext(postgres.ConnectionString);
        var store = new PostgresPresentationRevisionStore(context, TimeProvider.System);

        (await store.GetHeadAsync(stranger.Id, presentation.Id)).Should().BeNull();
        (await store.ListAsync(stranger.Id, presentation.Id, 1, 25)).Should().BeNull();
        (await store.GetAsync(stranger.Id, presentation.Id, 1)).Should().BeNull();
        (await store.TryAppendAsync(stranger.Id, presentation.Id, 1,
            new NewRevision(presentation.Script, RevisionSources.LiveEdit, "hijack", 1, null, [0], stranger.Id)))
            .Should().Be(new AppendResult.NotFound());
        (await store.TryAppendAsync(owner.Id, "prs_missing_revision_store", 1,
            new NewRevision(presentation.Script, RevisionSources.LiveEdit, "missing", 1, null, [0], owner.Id)))
            .Should().Be(new AppendResult.NotFound());

        (await store.GetHeadAsync(owner.Id, presentation.Id))!.Version.Should().Be(1);
        (await context.PresentationRevisions.CountAsync(row => row.PresentationId == presentation.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Cancelled_append_writes_nothing()
    {
        var (owner, presentation) = await SeedAsync(postgres.ConnectionString);
        await using var context = CreateContext(postgres.ConnectionString);
        var store = new PostgresPresentationRevisionStore(context, TimeProvider.System);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => store.TryAppendAsync(owner.Id, presentation.Id, 1,
            new NewRevision(presentation.Script, RevisionSources.LiveEdit, "cancelled", 1, null, [0], owner.Id), cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await using var readContext = CreateContext(postgres.ConnectionString);
        (await readContext.Presentations.AsNoTracking().SingleAsync(row => row.Id == presentation.Id)).Version.Should().Be(1);
        (await readContext.PresentationRevisions.CountAsync(row => row.PresentationId == presentation.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Migration_backfills_v1_and_resets_version()
    {
        var connectionString = await CreateDatabaseAsync();
        await MigrateToAsync(connectionString, MigrationBefore);
        var owner = Guid.NewGuid().ToString("N");
        var script = await SampleScriptAsync();
        var otherScript = script.Replace(OriginalSentence, "A different presentation.", StringComparison.Ordinal);
        await ExecuteAsync(connectionString, $"""
            INSERT INTO users (id, email, role, is_disabled, auth_method, created_at, updated_at)
            VALUES ('{owner}', '{owner}@backfill.test', 'user', false, 'test', now(), now());
            INSERT INTO presentations (id, owner_id, slug, title, deck, driver, script, slide_count, version, created_at, updated_at)
            VALUES ('prs_backfill_a', '{owner}', 'a', 'A', 'decks/sample/index.html', 'auto', @a, 3, 3, now(), '2026-01-02T03:04:05Z'),
                   ('prs_backfill_b', '{owner}', 'b', 'B', 'decks/sample/index.html', 'auto', @b, 3, 1, now(), '2026-02-03T04:05:06Z');
            """, ("a", script), ("b", otherScript));

        await MigrateToAsync(connectionString, null);

        await using var context = CreateContext(connectionString);
        var presentations = await context.Presentations.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
        presentations.Should().OnlyContain(row => row.Version == 1);
        var revisions = await context.PresentationRevisions.AsNoTracking().OrderBy(row => row.PresentationId).ToArrayAsync();
        revisions.Should().HaveCount(2);
        revisions.Select(row => row.PresentationId).Should().Equal("prs_backfill_a", "prs_backfill_b");
        revisions.Select(row => row.Script).Should().Equal(script, otherScript);
        revisions.Should().OnlyContain(row => row.Number == 1
            && row.Source == RevisionSources.Import
            && row.Summary == "Version before history"
            && row.BaseVersion == null
            && row.RevertedFrom == null
            && row.ChangedSlides.Length == 0
            && row.CreatedBy == null);
        revisions[0].CreatedAt.Should().Be(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

        var store = new PostgresPresentationRevisionStore(context, TimeProvider.System);
        (await store.TryAppendAsync(owner, "prs_backfill_a", 1,
            new NewRevision(otherScript, RevisionSources.LiveEdit, "after backfill", 1, null, [0], owner)))
            .Should().Be(new AppendResult.Applied(2));
    }

    [Fact]
    public async Task Down_on_backfilled_data_drops_the_table()
    {
        var connectionString = await CreateDatabaseAsync();
        await MigrateToAsync(connectionString, null);
        await SeedAsync(connectionString);

        await MigrateToAsync(connectionString, MigrationBefore);

        (await ScalarAsync(connectionString, "SELECT to_regclass('presentation_revisions')::text;")).Should().BeNull();
        (await ScalarAsync(connectionString, "SELECT count(*) FROM presentations WHERE version = 1;")).Should().Be("1");
    }

    [Fact]
    public async Task Down_with_history_is_refused_and_keeps_the_table()
    {
        var connectionString = await CreateDatabaseAsync();
        await MigrateToAsync(connectionString, null);
        var (owner, presentation) = await SeedAsync(connectionString);
        await using (var context = CreateContext(connectionString))
        {
            (await new PostgresPresentationRevisionStore(context, TimeProvider.System).TryAppendAsync(owner.Id, presentation.Id, 1,
                new NewRevision(presentation.Script.Replace(OriginalSentence, "History.", StringComparison.Ordinal),
                    RevisionSources.LiveEdit, "history", 1, null, [0], owner.Id)))
                .Should().Be(new AppendResult.Applied(2));
        }

        var act = () => MigrateToAsync(connectionString, MigrationBefore);

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.MessageText.Should().Be(PresentationRevisions.HistoryGuardMessage);
        (await ScalarAsync(connectionString, "SELECT to_regclass('presentation_revisions')::text;")).Should().Be("presentation_revisions");
        (await ScalarAsync(connectionString, "SELECT count(*) FROM presentation_revisions;")).Should().Be("2");
        await using var check = CreateContext(connectionString);
        (await check.Database.GetAppliedMigrationsAsync()).Should().Contain(migration => migration.EndsWith("_PresentationRevisions"));
    }

    private async Task<AppendResult> AppendAsync(string ownerId, string presentationId, int expected, NewRevision revision)
    {
        await using var context = CreateContext(postgres.ConnectionString);
        return await new PostgresPresentationRevisionStore(context, TimeProvider.System)
            .TryAppendAsync(ownerId, presentationId, expected, revision);
    }

    private static async Task<(User Owner, Presentation Presentation)> SeedAsync(string connectionString)
    {
        var owner = await SeedUserAsync(connectionString);
        var script = await SampleScriptAsync();
        await using var context = CreateContext(connectionString);
        var presentation = new Presentation
        {
            OwnerId = owner.Id,
            Slug = Guid.NewGuid().ToString("N"),
            Title = "Revision fixture",
            Deck = "decks/sample/index.html",
            Driver = "auto",
            Script = script,
            SlideCount = 3,
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        context.Presentations.Add(presentation);
        context.PresentationRevisions.Add(new PresentationRevision
        {
            PresentationId = presentation.Id,
            Number = 1,
            Script = script,
            Source = RevisionSources.Import,
            Summary = "Imported",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        return (owner, presentation);
    }

    private static async Task<User> SeedUserAsync(string connectionString)
    {
        await using var context = CreateContext(connectionString);
        await context.Database.MigrateAsync();
        var user = new User
        {
            Email = $"{Guid.NewGuid():N}@revisions.test",
            DisplayName = "Revision owner",
            AuthMethod = "test",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    /// <summary>A fresh database in the shared container, so migration Up/Down tests never touch the shared schema.</summary>
    private async Task<string> CreateDatabaseAsync()
    {
        var name = "revisions_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(postgres.ConnectionString, $"CREATE DATABASE {name};");
        return new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = name }.ConnectionString;
    }

    private static async Task MigrateToAsync(string connectionString, string? target)
    {
        await using var context = CreateContext(connectionString);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    private static async Task ExecuteAsync(string connectionString, string sql, params (string Name, string Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : value.ToString();
    }

    private static Task<string> SampleScriptAsync() =>
        File.ReadAllTextAsync(Path.Combine(FindRepositoryRoot(), "presentations", "sample.md"));

    private static PresenterAiDbContext CreateContext(string connectionString) => new(
        new DbContextOptionsBuilder<PresenterAiDbContext>().UseNpgsql(connectionString).Options);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "PresenterAi.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("PresenterAi.slnx was not found above the test assembly.");
    }
}
