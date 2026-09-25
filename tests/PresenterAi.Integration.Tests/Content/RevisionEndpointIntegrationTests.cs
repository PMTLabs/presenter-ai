using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Application.Scripts.Revisions;
using PresenterAi.Infrastructure.Persistence;
using PresenterAi.Integration.Tests.Support;
using Xunit;

namespace PresenterAi.Integration.Tests.Content;

/// <summary>
/// Plan 010 T4 (Postgres half, run in T11): <c>/v1/presentations/{id}/revisions</c> through the real API with the
/// Postgres store and the real <see cref="ScriptRevisionService"/> behind revert.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class RevisionEndpointIntegrationTests(PostgresFixture postgres, RedisFixture redis)
{
    private const string Edited = "Hello everyone, and welcome to the revised talk.";

    [Fact]
    public async Task List_detail_and_revert_round_trip_on_postgres()
    {
        var (owner, pid, v1) = await TrainingDb.SeedAsync(postgres.ConnectionString);
        var v2 = v1.Replace(TrainingDb.OpeningSentence, Edited, StringComparison.Ordinal);
        (await TrainingDb.AppendAsync(postgres.ConnectionString, owner, pid, 1, v2, RevisionSources.LiveEdit, "Welcome revised"))
            .Should().Be(new AppendResult.Applied(2));
        await using var factory = new IntegrationApiFactory(postgres, redis);
        using var client = factory.CreateAuthenticatedClient(owner);

        var list = (await client.GetFromJsonAsync<JsonObject>($"/v1/presentations/{pid}/revisions"))!;
        list["total"]!.GetValue<int>().Should().Be(2);
        list["items"]!.AsArray().Select(i => (i!["number"]!.GetValue<int>(), i["source"]!.GetValue<string>(),
                i["summary"]!.GetValue<string>(), i["isCurrent"]!.GetValue<bool>()))
            .Should().Equal((2, "live_edit", "Welcome revised", true), (1, "import", "Imported", false));

        var detail = (await client.GetFromJsonAsync<JsonObject>($"/v1/presentations/{pid}/revisions/2"))!;
        var change = detail["changes"]!.AsArray().Should().ContainSingle().Subject!;
        change["slideIndex"]!.GetValue<int>().Should().Be(0);
        change["before"]!.GetValue<string>().Should().StartWith(TrainingDb.OpeningSentence);
        change["after"]!.GetValue<string>().Should().StartWith(Edited);
        detail["slides"]!.AsArray().Should().HaveCount(3);

        var revert = await client.PostAsync($"/v1/presentations/{pid}/revisions/1/revert", null);
        revert.StatusCode.Should().Be(HttpStatusCode.Created);
        revert.Headers.Location!.ToString().Should().EndWith($"/v1/presentations/{pid}/revisions/3");
        var body = (await revert.Content.ReadFromJsonAsync<JsonObject>())!;
        body["revision"]!["number"]!.GetValue<int>().Should().Be(3);
        body["revision"]!["source"]!.GetValue<string>().Should().Be("revert");
        body["revision"]!["revertedFrom"]!.GetValue<int>().Should().Be(1);
        body["revision"]!["baseVersion"]!.GetValue<int>().Should().Be(2);
        body["revision"]!["changedSlides"]!.AsArray().Select(n => n!.GetValue<int>()).Should().Equal(0);
        body["revision"]!["isCurrent"]!.GetValue<bool>().Should().BeTrue();
        body["pendingEdits"]!.AsArray().Should().BeEmpty();

        (await TrainingDb.HeadAsync(postgres.ConnectionString, pid)).Should().Be((3, v1));
        var rows = await TrainingDb.RowsAsync(postgres.ConnectionString, pid);
        rows.Select(r => r.Script).Should().Equal(v1, v2, v1);
        rows[2].CreatedBy.Should().Be(owner);
        var presentation = (await client.GetFromJsonAsync<JsonObject>($"/v1/presentations/{pid}"))!;
        presentation["slides"]![0]!["narration"]!.GetValue<string>().Should()
            .StartWith(TrainingDb.OpeningSentence + " This", "the presentation reader serves the durable head");
    }

    [Fact]
    public async Task Unknown_revision_and_other_owner_are_not_found()
    {
        var (owner, pid, _) = await TrainingDb.SeedAsync(postgres.ConnectionString);
        var (stranger, _, _) = await TrainingDb.SeedAsync(postgres.ConnectionString);
        await using var factory = new IntegrationApiFactory(postgres, redis);
        using var client = factory.CreateAuthenticatedClient(owner);
        using var other = factory.CreateAuthenticatedClient(stranger);

        await ShouldBeProblemAsync(await client.GetAsync($"/v1/presentations/{pid}/revisions/9"), HttpStatusCode.NotFound, "revision.not_found");
        await ShouldBeProblemAsync(await client.PostAsync($"/v1/presentations/{pid}/revisions/9/revert", null),
            HttpStatusCode.NotFound, "revision.not_found");
        await ShouldBeProblemAsync(await other.GetAsync($"/v1/presentations/{pid}/revisions"), HttpStatusCode.NotFound, "presentation.not_found");
        await ShouldBeProblemAsync(await other.GetAsync($"/v1/presentations/{pid}/revisions/1"), HttpStatusCode.NotFound, "presentation.not_found");
        await ShouldBeProblemAsync(await other.PostAsync($"/v1/presentations/{pid}/revisions/1/revert", null),
            HttpStatusCode.NotFound, "presentation.not_found");
        (await TrainingDb.RowsAsync(postgres.ConnectionString, pid)).Should().ContainSingle();
    }

    [Fact]
    public async Task Revert_that_loses_the_cas_twice_is_409_and_writes_nothing()
    {
        var (owner, pid, v1) = await TrainingDb.SeedAsync(postgres.ConnectionString);
        var v2 = v1.Replace(TrainingDb.OpeningSentence, Edited, StringComparison.Ordinal);
        await TrainingDb.AppendAsync(postgres.ConnectionString, owner, pid, 1, v2, RevisionSources.LiveEdit);
        // Another writer commits right before each of the revert's two CAS attempts.
        var racer = new RacingWriter(postgres.ConnectionString, owner, pid, v1, v2, races: 2);
        await using var factory = new IntegrationApiFactory(postgres, redis)
        {
            TestServices = services => services.ConfigureDbContext<PresenterAiDbContext>(options => options.AddInterceptors(racer))
        };
        using var client = factory.CreateAuthenticatedClient(owner);

        await ShouldBeProblemAsync(await client.PostAsync($"/v1/presentations/{pid}/revisions/1/revert", null),
            HttpStatusCode.Conflict, "concurrency.conflict");

        racer.Raced.Should().Be(2);
        var rows = await TrainingDb.RowsAsync(postgres.ConnectionString, pid);
        rows.Select(r => r.Source).Should().Equal(RevisionSources.Import, RevisionSources.LiveEdit, RevisionSources.Import, RevisionSources.Import);
        (await TrainingDb.HeadAsync(postgres.ConnectionString, pid)).Version.Should().Be(4);
    }

    private static async Task ShouldBeProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.Should().Be(status);
        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        problem["code"]!.GetValue<string>().Should().Be(code);
    }

    /// <summary>Before the API's version CAS <c>UPDATE</c> runs, commits a competing revision through its own connection.</summary>
    private sealed class RacingWriter(string connectionString, string owner, string pid, string v1, string v2, int races)
        : DbCommandInterceptor
    {
        private int _remaining = races;
        private int _raced;

        public int Raced => Volatile.Read(ref _raced);

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await RaceAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await RaceAsync(command);
            return result;
        }

        private async Task RaceAsync(DbCommand command)
        {
            if (!command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                || !command.CommandText.Contains("presentations", StringComparison.Ordinal)
                || Interlocked.Decrement(ref _remaining) < 0)
                return;
            var head = await TrainingDb.HeadAsync(connectionString, pid);
            var script = head.Script == v1 ? v2 : v1;
            (await TrainingDb.AppendAsync(connectionString, owner, pid, head.Version, script, RevisionSources.Import))
                .Should().BeOfType<AppendResult.Applied>();
            Interlocked.Increment(ref _raced);
        }
    }
}
