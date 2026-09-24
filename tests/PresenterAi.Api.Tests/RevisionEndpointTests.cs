using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Api.Tests.Infrastructure;
using PresenterAi.Application.Scripts.Revisions;

namespace PresenterAi.Api.Tests;

/// <summary>
/// Plan 010 T4: <c>/v1/presentations/{id}/revisions</c> over the in-memory store and the fake revert port. The
/// Postgres-backed flow is <c>RevisionEndpointIntegrationTests</c> (T11).
/// </summary>
public sealed class RevisionEndpointTests
{
    private const string Id = "sample";
    private const string Original = "Hello everyone, and welcome.";
    private const string Edited = "Hello everyone, and welcome to the revised talk.";

    [Fact]
    public async Task List_is_newest_first_with_source_and_summary()
    {
        using var factory = new ApiFactory();
        var store = await SeedHistoryAsync(factory);
        using var client = factory.CreateAuthenticatedClient();

        var body = await GetJsonAsync(client, $"/v1/presentations/{Id}/revisions");

        body["page"]!.GetValue<int>().Should().Be(1);
        body["pageSize"]!.GetValue<int>().Should().Be(25);
        body["total"]!.GetValue<int>().Should().Be(3);
        var items = body["items"]!.AsArray();
        items.Select(item => item!["number"]!.GetValue<int>()).Should().Equal(3, 2, 1);
        items.Select(item => item!["source"]!.GetValue<string>()).Should().Equal("revert", "live_edit", "import");
        items.Select(item => item!["summary"]!.GetValue<string>()).Should().Equal(
            "Reverted to v1", "Welcome mentions the revised talk", InMemoryPresentationRevisionStore.SeedSummary);
        items.Select(item => item!["isCurrent"]!.GetValue<bool>()).Should().Equal(true, false, false);
        items[0]!["revertedFrom"]!.GetValue<int>().Should().Be(1);
        items[0]!["baseVersion"]!.GetValue<int>().Should().Be(2);
        items[1]!["changedSlides"]!.AsArray().Select(value => value!.GetValue<int>()).Should().Equal(0);
        items[1]!["createdAt"]!.GetValue<DateTimeOffset>().Should().BeAfter(DateTimeOffset.UnixEpoch);

        var paged = await GetJsonAsync(client, $"/v1/presentations/{Id}/revisions?page=2&pageSize=2");
        paged["items"]!.AsArray().Select(item => item!["number"]!.GetValue<int>()).Should().Equal(1);
        paged["total"]!.GetValue<int>().Should().Be(3);

        var invalid = await client.GetAsync($"/v1/presentations/{Id}/revisions?pageSize=101");
        await ShouldBeProblemAsync(invalid, HttpStatusCode.BadRequest, "validation.failed");
        (await store.ListAsync("test-user", Id, 1, 25))!.Total.Should().Be(3);
    }

    [Fact]
    public async Task Detail_has_before_and_after_per_changed_slide()
    {
        using var factory = new ApiFactory();
        await SeedHistoryAsync(factory);
        using var client = factory.CreateAuthenticatedClient();

        var edit = await GetJsonAsync(client, $"/v1/presentations/{Id}/revisions/2");

        edit["number"]!.GetValue<int>().Should().Be(2);
        edit["source"]!.GetValue<string>().Should().Be("live_edit");
        edit["isCurrent"]!.GetValue<bool>().Should().BeFalse();
        var slides = edit["slides"]!.AsArray();
        slides.Should().HaveCount(3);
        slides[0]!["index"]!.GetValue<int>().Should().Be(0);
        slides[0]!["number"]!.GetValue<int>().Should().Be(1);
        slides[0]!["title"]!.GetValue<string>().Should().Be("Sample deck");
        slides[0]!["narration"]!.GetValue<string>().Should().StartWith(Edited);
        var changes = edit["changes"]!.AsArray();
        changes.Should().ContainSingle();
        changes[0]!["slideIndex"]!.GetValue<int>().Should().Be(0);
        changes[0]!["title"]!.GetValue<string>().Should().Be("Sample deck");
        changes[0]!["before"]!.GetValue<string>().Should().StartWith(Original);
        changes[0]!["after"]!.GetValue<string>().Should().StartWith(Edited);

        var revert = await GetJsonAsync(client, $"/v1/presentations/{Id}/revisions/3");
        revert["isCurrent"]!.GetValue<bool>().Should().BeTrue();
        var revertChange = revert["changes"]!.AsArray().Should().ContainSingle().Subject!;
        revertChange["before"]!.GetValue<string>().Should().StartWith(Edited);
        revertChange["after"]!.GetValue<string>().Should().StartWith(Original);

        var first = await GetJsonAsync(client, $"/v1/presentations/{Id}/revisions/1");
        first["changes"]!.AsArray().Should().BeEmpty("the first version has no base version");
        first["slides"]!.AsArray().Should().HaveCount(3);
    }

    [Fact]
    public async Task Revert_returns_201_with_new_revision_and_pending_edits()
    {
        using var factory = new ApiFactory();
        var createdAt = new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);
        factory.RevisionService.OnRevert = call => new RevertResult.Reverted(
            new RevisionInfo(4, RevisionSources.Revert, createdAt, "Reverted to v1", 3, call.Number, [0], call.UserId),
            [new PendingEdit("edit_4", [2], ScriptEditStatus.Processing), new PendingEdit("edit_5", [0, 1], ScriptEditStatus.Queued)]);
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsync($"/v1/presentations/{Id}/revisions/1/revert", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.OriginalString.Should().Be($"/v1/presentations/{Id}/revisions/4");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var revision = body["revision"]!;
        revision["number"]!.GetValue<int>().Should().Be(4);
        revision["source"]!.GetValue<string>().Should().Be("revert");
        revision["revertedFrom"]!.GetValue<int>().Should().Be(1);
        revision["baseVersion"]!.GetValue<int>().Should().Be(3);
        revision["summary"]!.GetValue<string>().Should().Be("Reverted to v1");
        revision["isCurrent"]!.GetValue<bool>().Should().BeTrue();
        revision["createdAt"]!.GetValue<DateTimeOffset>().Should().Be(createdAt);
        var pending = body["pendingEdits"]!.AsArray();
        pending.Select(edit => edit!["id"]!.GetValue<string>()).Should().Equal("edit_4", "edit_5");
        pending.Select(edit => edit!["status"]!.GetValue<string>()).Should().Equal("processing", "queued");
        pending[1]!["slideIndexes"]!.AsArray().Select(value => value!.GetValue<int>()).Should().Equal(0, 1);
        factory.RevisionService.Reverts.Should().Equal(
            new PresenterAi.TestSupport.FakeScriptRevisionService.RevertCall("test-user", Id, 1, "test-user"));
    }

    [Fact]
    public async Task Second_revert_conflict_is_409()
    {
        using var factory = new ApiFactory();
        factory.RevisionService.OnRevert = _ => new RevertResult.Conflict();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsync($"/v1/presentations/{Id}/revisions/1/revert", content: null);

        await ShouldBeProblemAsync(response, HttpStatusCode.Conflict, "concurrency.conflict");
    }

    [Fact]
    public async Task Unknown_revision_is_revision_not_found()
    {
        using var factory = new ApiFactory();
        factory.RevisionService.OnRevert = _ => new RevertResult.RevisionNotFound();
        using var client = factory.CreateAuthenticatedClient();

        await ShouldBeProblemAsync(
            await client.GetAsync($"/v1/presentations/{Id}/revisions/99"), HttpStatusCode.NotFound, "revision.not_found");
        await ShouldBeProblemAsync(
            await client.PostAsync($"/v1/presentations/{Id}/revisions/99/revert", content: null),
            HttpStatusCode.NotFound,
            "revision.not_found");
        factory.RevisionService.Reverts.Single().Number.Should().Be(99);
    }

    [Fact]
    public async Task Other_owner_revert_is_presentation_not_found()
    {
        using var factory = new ApiFactory();
        await SeedHistoryAsync(factory);
        factory.RevisionService.OnRevert = _ => new RevertResult.PresentationNotFound();
        using var stranger = factory.CreateAuthenticatedClient(userId: "other-user", email: "other@presenter-ai.local");

        await ShouldBeProblemAsync(
            await stranger.GetAsync($"/v1/presentations/{Id}/revisions"), HttpStatusCode.NotFound, "presentation.not_found");
        await ShouldBeProblemAsync(
            await stranger.GetAsync($"/v1/presentations/{Id}/revisions/1"), HttpStatusCode.NotFound, "presentation.not_found");
        await ShouldBeProblemAsync(
            await stranger.PostAsync($"/v1/presentations/{Id}/revisions/1/revert", content: null),
            HttpStatusCode.NotFound,
            "presentation.not_found");
        factory.RevisionService.Reverts.Single().Should().Be(
            new PresenterAi.TestSupport.FakeScriptRevisionService.RevertCall("other-user", Id, 1, "other-user"));

        using var owner = factory.CreateAuthenticatedClient();
        await ShouldBeProblemAsync(
            await owner.GetAsync("/v1/presentations/no-such-presentation/revisions"), HttpStatusCode.NotFound, "presentation.not_found");
        await ShouldBeProblemAsync(
            await owner.GetAsync("/v1/presentations/no-such-presentation/revisions/1"), HttpStatusCode.NotFound, "presentation.not_found");
    }

    [Fact]
    public async Task Revision_endpoints_require_authentication()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        (await client.GetAsync($"/v1/presentations/{Id}/revisions")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync($"/v1/presentations/{Id}/revisions/1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsync($"/v1/presentations/{Id}/revisions/1/revert", content: null)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        factory.RevisionService.Reverts.Should().BeEmpty();
    }

    /// <summary>v1 = the file (seeded), v2 = a live edit of slide 1, v3 = a revert to v1.</summary>
    private static async Task<IPresentationRevisionStore> SeedHistoryAsync(ApiFactory factory)
    {
        var store = factory.Services.GetRequiredService<IPresentationRevisionStore>();
        var head = await store.GetHeadAsync("test-user", Id);
        head.Should().NotBeNull();
        head!.Markdown.Should().Contain(Original);
        var edited = head.Markdown.Replace(Original, Edited, StringComparison.Ordinal);
        (await store.TryAppendAsync("test-user", Id, 1,
            new NewRevision(edited, RevisionSources.LiveEdit, "Welcome mentions the revised talk", 1, null, [0], "test-user")))
            .Should().Be(new AppendResult.Applied(2));
        (await store.TryAppendAsync("test-user", Id, 2,
            new NewRevision(head.Markdown, RevisionSources.Revert, "Reverted to v1", 2, 1, [0], "test-user")))
            .Should().Be(new AppendResult.Applied(3));
        return store;
    }

    private static async Task<JsonNode> GetJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonNode.Parse(text)!;
    }

    private static async Task ShouldBeProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, text);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = JsonNode.Parse(text)!;
        body["code"]!.GetValue<string>().Should().Be(code);
        body["status"]!.GetValue<int>().Should().Be((int)status);
    }
}
