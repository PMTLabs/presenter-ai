using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using PresenterAi.Infrastructure.Persistence;
using PresenterAi.Infrastructure.Persistence.Entities;
using PresenterAi.Infrastructure.Tests.Live;
using PresenterAi.Integration.Tests.Support;
using Xunit;

namespace PresenterAi.Integration.Tests.Cli;

[Collection(IntegrationCollection.Name)]
public sealed class RunCommandTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Run_with_owner_records_a_session()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        var owner = await SeedUserAsync();
        var presentation = await ImportSampleAsync(owner);
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await PresenterAi.Cli.Program.RunAsync(
            ["run", "sample", "--owner", owner.Email, "--stop-after-slide", "2", "--max-seconds", "30"],
            Configuration(fake), output, error, CancellationToken.None);

        exit.Should().Be(0, error.ToString());
        await WaitForAsync(async () => await SessionCountFinalisedAsync(owner.Id) == 1);
        await using var context = CreateContext();
        var session = await context.Sessions.Include(item => item.Turns).SingleAsync(item => item.UserId == owner.Id);
        session.PresentationId.Should().Be(presentation.Id);
        session.EndedAt.Should().NotBeNull();
        session.Upstream.Should().Be("primary");
        session.Turns.Should().NotBeEmpty();
        session.Turns.Should().OnlyContain(turn => turn.SlideNo.HasValue);
    }

    [Fact]
    public async Task Run_with_an_unknown_owner_exits_non_zero()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        // An imported existing owner makes this detect a mutation that falls back to a different user.
        var existing = await SeedUserAsync();
        await ImportSampleAsync(existing);
        var before = await SnapshotAsync();
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await PresenterAi.Cli.Program.RunAsync(
            ["run", "sample", "--owner", "missing-run-owner@example.test"],
            Configuration(fake), output, error, CancellationToken.None);

        exit.Should().Be(1);
        error.ToString().Should().Contain("Unknown owner");
        (await SnapshotAsync()).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Owner_backed_run_lookup_on_a_missing_database_exits_1_with_a_single_safe_error_line()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        var error = new StringWriter();

        var exit = await PresenterAi.Cli.Program.RunAsync(
            ["run", "sample", "--owner", "owner@example.test"],
            MissingDatabaseConfiguration(fake), new StringWriter(), error, CancellationToken.None);

        exit.Should().Be(1);
        error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Should().ContainSingle().Which.Should().StartWith("Database error: database");
    }

    [Fact]
    public async Task Run_with_a_slug_the_owner_does_not_have_exits_non_zero()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        var owner = await SeedUserAsync();
        var other = await SeedUserAsync();
        await ImportSampleAsync(other);
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await PresenterAi.Cli.Program.RunAsync(
            ["run", "sample", "--owner", owner.Email],
            Configuration(fake), output, error, CancellationToken.None);

        exit.Should().Be(1);
        error.ToString().Should().Contain("presentation not found");
        await using var context = CreateContext();
        (await context.Sessions.CountAsync(session => session.UserId == owner.Id)).Should().Be(0);
    }

    private async Task<User> SeedUserAsync()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var user = new User
        {
            Email = $"{Guid.NewGuid():N}@run.test",
            DisplayName = "Run owner",
            AuthMethod = "test",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    private async Task<Presentation> ImportSampleAsync(User owner)
    {
        var root = FindRepositoryRoot();
        var exit = await PresenterAi.Cli.Program.RunAsync(
            ["import", Path.Combine(root, "presentations", "sample.md"), "--owner", owner.Email, "--content-root", root],
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = postgres.ConnectionString
            }).Build(), new StringWriter(), new StringWriter(), CancellationToken.None);
        exit.Should().Be(0);
        await using var context = CreateContext();
        return await context.Presentations.SingleAsync(presentation => presentation.OwnerId == owner.Id);
    }

    private IConfiguration Configuration(FakeLiveServer fake, string? connectionString = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = connectionString ?? postgres.ConnectionString,
            ["Upstream:Endpoint"] = fake.Url,
            ["Upstream:Key"] = "integration-test-only",
            ["Presenter:AdvanceSilenceMs"] = "200"
        }).Build();

    private IConfiguration MissingDatabaseConfiguration(FakeLiveServer fake)
    {
        var connection = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            Database = "presenter_ai_missing_" + Guid.NewGuid().ToString("N")
        };
        return Configuration(fake, connection.ConnectionString);
    }

    private async Task<int> SessionCountFinalisedAsync(string ownerId)
    {
        await using var context = CreateContext();
        return await context.Sessions.CountAsync(session => session.UserId == ownerId && session.EndedAt != null);
    }

    private async Task<PersistenceSnapshot> SnapshotAsync()
    {
        await using var context = CreateContext();
        return new PersistenceSnapshot(
            await context.Presentations.OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync(),
            await context.Sessions.OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync());
    }

    private PresenterAiDbContext CreateContext() => new(new DbContextOptionsBuilder<PresenterAiDbContext>()
        .UseNpgsql(postgres.ConnectionString)
        .Options);

    private sealed record PersistenceSnapshot(string[] PresentationIds, string[] SessionIds);

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }

        (await condition()).Should().BeTrue();
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "PresenterAi.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("PresenterAi.slnx was not found above the test assembly.");
    }
}
