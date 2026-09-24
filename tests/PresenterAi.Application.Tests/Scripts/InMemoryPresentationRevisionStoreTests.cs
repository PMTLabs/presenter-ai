using PresenterAi.Application.Scripts;
using PresenterAi.Application.Scripts.Revisions;
using Xunit;

namespace PresenterAi.Application.Tests.Scripts;

public sealed class InMemoryPresentationRevisionStoreTests
{
    private const string Owner = "usr_owner";
    private const string Id = "prs_sample";

    [Fact]
    public async Task Append_with_stale_version_conflicts()
    {
        var original = ReadFixture("sample.md");
        var store = new InMemoryPresentationRevisionStore((owner, id, _) =>
            Task.FromResult<string?>(owner == Owner && id == Id ? original : null));
        var head = await store.GetHeadAsync(Owner, Id);
        Assert.NotNull(head);
        Assert.Equal(1, head.Version);
        var edited = WithNarration(head.Script, 0, "Edited narration one.");
        var competing = WithNarration(head.Script, 0, "Competing narration.");

        var first = await store.TryAppendAsync(Owner, Id, 1, Revision(edited, 1));
        var stale = await store.TryAppendAsync(Owner, Id, 1, Revision(competing, 1));

        Assert.Equal(new AppendResult.Applied(2), first);
        Assert.Equal(new AppendResult.Conflict(2), stale);
        var after = await store.GetHeadAsync(Owner, Id);
        Assert.Equal(2, after!.Version);
        Assert.Equal(edited, after.Markdown);
        Assert.Equal("Edited narration one.", after.Slides[0].Narration);
        var list = await store.ListAsync(Owner, Id, 1, 10);
        Assert.Equal([2, 1], list!.Items.Select(r => r.Number));
        Assert.Equal(2, list.CurrentVersion);
        Assert.Equal(original, (await store.GetAsync(Owner, Id, 1))!.Script);
        Assert.Equal(RevisionSources.LiveEdit, (await store.GetAsync(Owner, Id, 2))!.Info.Source);
        Assert.Null(await store.GetAsync(Owner, Id, 3));

        // Another owner cannot see or write the presentation.
        Assert.Null(await store.GetHeadAsync("usr_other", Id));
        Assert.IsType<AppendResult.NotFound>(await store.TryAppendAsync("usr_other", Id, 2, Revision(competing, 2)));
        Assert.Equal(2, (await store.GetHeadAsync(Owner, Id))!.Version);
    }

    [Fact]
    public async Task Cancelled_append_between_cas_and_commit_writes_nothing()
    {
        var store = new InMemoryPresentationRevisionStore();
        store.Seed(Owner, Id, ReadFixture("sample.md"));
        var head = (await store.GetHeadAsync(Owner, Id))!;
        using var cts = new CancellationTokenSource();
        store.BeforeCommitAsync = (_, _) =>
        {
            cts.Cancel();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.TryAppendAsync(Owner, Id, 1, Revision(WithNarration(head.Script, 0, "Never committed."), 1), cts.Token));

        Assert.Equal(1, (await store.GetHeadAsync(Owner, Id))!.Version);
        Assert.Equal(1, (await store.ListAsync(Owner, Id, 1, 10))!.Total);
    }

    private static NewRevision Revision(string markdown, int baseVersion) =>
        new(markdown, RevisionSources.LiveEdit, "Changed slide 1", baseVersion, null, [0], Owner);

    private static string WithNarration(PresentationScript script, int index, string narration) =>
        ScriptWriter.Format(script with
        {
            Slides = script.Slides.Select(s => s.Index == index ? s with { Narration = narration } : s).ToArray()
        });

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
