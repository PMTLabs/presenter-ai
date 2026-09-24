using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Scripts;
using PresenterAi.Application.Scripts.Revisions;
using PresenterAi.Infrastructure.Content;
using PresenterAi.Infrastructure.Tests.Live;
using PresenterAi.Infrastructure.Training;
using PresenterAi.Integration.Tests.Support;
using Xunit;

namespace PresenterAi.Integration.Tests.Training;

/// <summary>
/// Plan 010 T11: each user flow end to end through the real API — <c>/ws</c> bridge, presenter, revision service,
/// Responses reviser, Postgres store and HTTP endpoints. Only the GPT-Live socket (<see cref="FakeLiveServer"/>) and the
/// Responses endpoint (<see cref="StubResponsesHandler"/>) are fake.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class LiveTrainingFlowTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task Production_container_resolves_training_services()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        await using var factory = TrainingFactory.Create(postgres, redis, fake.Url, new StubResponsesHandler());
        factory.ValidateContainer = true;

        factory.Services.GetRequiredService<IPresenter>().Should().BeOfType<Presenter>();
        var service = factory.Services.GetRequiredService<IScriptRevisionService>();
        service.Should().BeOfType<ScriptRevisionService>();
        service.IsAvailable.Should().BeTrue("the primary route has a delegation model");
        factory.Services.GetRequiredService<IScriptReviser>().Should().BeOfType<ResponsesScriptReviser>();
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IPresentationRevisionStore>()
            .Should().BeOfType<PostgresPresentationRevisionStore>();
    }

    [Fact]
    public async Task Voice_edit_confirmed_over_ws_is_persisted_and_used_by_the_next_talk()
    {
        var (owner, pid, v1) = await TrainingDb.SeedAsync(postgres.ConnectionString);
        await using var fake = await FakeLiveServer.StartAsync();
        var responses = new StubResponsesHandler();
        await using var factory = TrainingFactory.Create(postgres, redis, fake.Url, responses);
        using var talk = await TalkClient.ConnectAsync(factory, owner);
        (await talk.StartAsync(pid))["version"]!.GetValue<int>().Should().Be(1);
        fake.ReceivedSnapshot().Single(m => m["type"]?.GetValue<string>() == "session.start").ToJsonString()
            .Should().Contain("\"revise_script\"");
        await talk.TrainerOnAsync();

        // AC1: the model calls revise_script; the presenter asks; "yes" confirms on the loop.
        await fake.SendFunctionCallAsync("d1", "call1", "revise_script", "{\"feedback\":\"Mention the 2025 figures.\"}");
        await talk.ReceiveUntilAsync(f => TalkClient.Type(f) == "log" && f["message"]!.ToString().Contains("waiting for yes"));
        await factory.SettleAsync();
        await factory.SettleAsync(TimeSpan.FromSeconds(8));
        responses.Inputs.Should().BeEmpty("nothing is sent before the owner confirms");
        await fake.SendEventAsync(new JsonObject
        {
            ["type"] = "session.input_transcript.delta", ["delta"] = "yes", ["start_ms"] = 900_000, ["end_ms"] = 900_100
        });
        await talk.ReceiveUntilAsync(f => TalkClient.Type(f) == "transcript" && f["delta"]!.ToString() == "yes");
        await factory.SettleAsync();
        await factory.SettleAsync(TimeSpan.FromMilliseconds(701));

        var applied = await talk.TerminalEditAsync();
        applied["status"]!.GetValue<string>().Should().Be(ScriptEditStatus.Applied);
        applied["version"]!.GetValue<int>().Should().Be(2);
        applied["slideIndexes"]!.AsArray().Select(n => n!.GetValue<int>()).Should().Equal(0);

        // The request the reviser actually posted: the confirmed feedback and the current slide as the only target.
        var input = responses.Inputs.Should().ContainSingle().Subject;
        input["request"]!["feedback"]!.GetValue<string>().Should().Be("Mention the 2025 figures.");
        input["request"]!["exchange"].Should().BeNull();
        input["targets"]!.AsArray().Select(t => t!["number"]!.GetValue<int>()).Should().Equal(1);
        input["context"]!["recent"].Should().NotBeNull();

        // AC3: v2 persisted, v1 kept; the presenter replays slide 1 with the new narration.
        var newNarration = ScriptParser.Parse(v1, pid).Slides[0].Narration + " Mention the 2025 figures.";
        var (version, script) = await TrainingDb.HeadAsync(postgres.ConnectionString, pid);
        version.Should().Be(2);
        ScriptParser.Parse(script, pid).Slides[0].Narration.Should().Be(newNarration);
        var rows = await TrainingDb.RowsAsync(postgres.ConnectionString, pid);
        rows.Select(r => (r.Number, r.Source, r.BaseVersion)).Should().Equal(
            (1, RevisionSources.Import, (int?)null), (2, RevisionSources.LiveEdit, 1));
        rows[0].Script.Should().Be(v1);
        rows[1].ChangedSlides.Should().Equal(0);
        rows[1].CreatedBy.Should().Be(owner);
        await WaitForModelTextAsync(fake, "Mention the 2025 figures.", 0);

        // The next talk loads the durable head.
        await talk.SendAsync("{\"type\":\"end\"}");
        await talk.ReceiveUntilAsync(f => TalkClient.Type(f) == "closed");
        await talk.ReceiveUntilAsync(f => TalkClient.Type(f) == "state" && f["state"]!.ToString() == "idle");
        var mark = fake.ReceivedSnapshot().Count;
        (await talk.StartAsync(pid))["version"]!.GetValue<int>().Should().Be(2);
        await WaitForModelTextAsync(fake, "Mention the 2025 figures.", mark);
    }

    [Fact]
    public async Task Train_on_this_over_ws_persists_a_live_edit_for_the_selected_slide()
    {
        var (owner, pid, v1) = await TrainingDb.SeedAsync(postgres.ConnectionString);
        await using var fake = await FakeLiveServer.StartAsync();
        var responses = new StubResponsesHandler();
        await using var factory = TrainingFactory.Create(postgres, redis, fake.Url, responses);
        using var talk = await TalkClient.ConnectAsync(factory, owner);
        await talk.StartAsync(pid);
        await talk.TrainerOnAsync();

        // AC6: the exchange happened on slide 3 while the talk shows slide 1.
        await talk.TrainTurnAsync("Which key ends the talk?", "Escape ends the session straight away.", 2);
        var applied = await talk.TerminalEditAsync();
        applied["status"]!.GetValue<string>().Should().Be(ScriptEditStatus.Applied);
        applied["slideIndexes"]!.AsArray().Select(n => n!.GetValue<int>()).Should().Equal(2);

        var input = responses.Inputs.Should().ContainSingle().Subject;
        input["request"]!["exchange"]!["question"]!.GetValue<string>().Should().Be("Which key ends the talk?");
        input["request"]!["exchange"]!["answer"]!.GetValue<string>().Should().Be("Escape ends the session straight away.");
        input["targets"]!.AsArray().Select(t => t!["number"]!.GetValue<int>()).Should().Equal(3);

        var rows = await TrainingDb.RowsAsync(postgres.ConnectionString, pid);
        rows.Should().HaveCount(2);
        rows[1].Source.Should().Be(RevisionSources.LiveEdit);
        rows[1].ChangedSlides.Should().Equal(2);
        var before = ScriptParser.Parse(v1, pid).Slides;
        var after = ScriptParser.Parse(rows[1].Script, pid).Slides;
        after[2].Narration.Should().EndWith("Escape ends the session straight away.");
        after.Take(2).Select(s => s.Narration).Should().Equal(before.Take(2).Select(s => s.Narration));
    }

    [Fact]
    public async Task Revert_over_http_during_a_talk_replays_and_persists()
    {
        var (owner, pid, v1) = await TrainingDb.SeedAsync(postgres.ConnectionString);
        await using var fake = await FakeLiveServer.StartAsync();
        var responses = new StubResponsesHandler();
        await using var factory = TrainingFactory.Create(postgres, redis, fake.Url, responses);
        using var talk = await TalkClient.ConnectAsync(factory, owner);
        await talk.StartAsync(pid);
        await talk.TrainerOnAsync();
        await talk.TrainTurnAsync("What else?", "It also has a dark mode.", 0);
        (await talk.TerminalEditAsync())["version"]!.GetValue<int>().Should().Be(2);
        await WaitForModelTextAsync(fake, "It also has a dark mode.", 0);

        // AC4: the revert is the new head immediately and the current slide replays with v1's narration.
        var mark = fake.ReceivedSnapshot().Count;
        using var http = factory.CreateAuthenticatedClient(owner);
        var response = await http.PostAsync($"/v1/presentations/{pid}/revisions/1/revert", null);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        body["revision"]!["number"]!.GetValue<int>().Should().Be(3);
        body["revision"]!["revertedFrom"]!.GetValue<int>().Should().Be(1);
        body["pendingEdits"]!.AsArray().Should().BeEmpty();
        (await talk.ReceiveUntilAsync(f => TalkClient.Type(f) == "script_version" && f["version"]!.GetValue<int>() == 3))
            ["trainerMode"]!.GetValue<bool>().Should().BeTrue();
        var original = ScriptParser.Parse(v1, pid).Slides[0].Narration;
        await WaitForModelTextAsync(fake, original[..60], mark);

        var (version, script) = await TrainingDb.HeadAsync(postgres.ConnectionString, pid);
        (version, script).Should().Be((3, v1));
        var rows = await TrainingDb.RowsAsync(postgres.ConnectionString, pid);
        rows.Select(r => (r.Source, r.RevertedFrom)).Should().Equal(
            (RevisionSources.Import, (int?)null), (RevisionSources.LiveEdit, null), (RevisionSources.Revert, 1));
        rows[1].Script.Should().Contain("It also has a dark mode.", "the reverted version stays in history");

        var list = (await http.GetFromJsonAsync<JsonObject>($"/v1/presentations/{pid}/revisions"))!;
        list["items"]!.AsArray().Select(i => (i!["number"]!.GetValue<int>(), i["isCurrent"]!.GetValue<bool>()))
            .Should().Equal((3, true), (2, false), (1, false));
    }

    [Fact]
    public async Task Revert_is_used_by_the_next_load()
    {
        var (owner, pid, v1) = await TrainingDb.SeedAsync(postgres.ConnectionString);
        var v2 = v1.Replace(TrainingDb.OpeningSentence, "Hello everyone, this is version two.", StringComparison.Ordinal);
        (await TrainingDb.AppendAsync(postgres.ConnectionString, owner, pid, 1, v2, RevisionSources.Import))
            .Should().Be(new AppendResult.Applied(2));
        await using var fake = await FakeLiveServer.StartAsync();
        await using var factory = TrainingFactory.Create(postgres, redis, fake.Url, new StubResponsesHandler());

        using var http = factory.CreateAuthenticatedClient(owner);
        (await http.PostAsync($"/v1/presentations/{pid}/revisions/1/revert", null)).StatusCode.Should().Be(HttpStatusCode.Created);

        using var talk = await TalkClient.ConnectAsync(factory, owner);
        (await talk.StartAsync(pid))["version"]!.GetValue<int>().Should().Be(3);
        await WaitForModelTextAsync(fake, TrainingDb.OpeningSentence, 0);
        fake.ReceivedSnapshot().Should().NotContain(m => m.ToJsonString().Contains("this is version two", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_queued_edits_and_a_revert_converge_with_all_rows_kept()
    {
        var (owner, pid, v1) = await TrainingDb.SeedAsync(postgres.ConnectionString);
        var v2 = v1.Replace("Finally, the keyboard.", "Finally, the keyboard in version two.", StringComparison.Ordinal);
        (await TrainingDb.AppendAsync(postgres.ConnectionString, owner, pid, 1, v2, RevisionSources.Import))
            .Should().Be(new AppendResult.Applied(2));
        await using var fake = await FakeLiveServer.StartAsync();
        var responses = new StubResponsesHandler();
        await using var factory = TrainingFactory.Create(postgres, redis, fake.Url, responses);
        using var talk = await TalkClient.ConnectAsync(factory, owner);
        (await talk.StartAsync(pid))["version"]!.GetValue<int>().Should().Be(2);
        await talk.TrainerOnAsync();

        var held = responses.HoldNext();
        await talk.TrainTurnAsync("First?", "Edit A text.", 0);
        var a = (await talk.ReceiveUntilAsync(f => TalkClient.Type(f) == "script_edit"))["id"]!.GetValue<string>();
        await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await talk.TrainTurnAsync("Second?", "Edit B text.", 1);
        var b = (await talk.ReceiveUntilAsync(f => TalkClient.Type(f) == "script_edit" && f["id"]!.GetValue<string>() != a))
            ["id"]!.GetValue<string>();

        // AC8: a revert while A is in the reviser and B is queued becomes the head at once and lists both.
        using var http = factory.CreateAuthenticatedClient(owner);
        var response = await http.PostAsync($"/v1/presentations/{pid}/revisions/1/revert", null);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        body["revision"]!["number"]!.GetValue<int>().Should().Be(3);
        body["pendingEdits"]!.AsArray().Select(p => (p!["id"]!.GetValue<string>(), p["status"]!.GetValue<string>()))
            .Should().Equal((a, ScriptEditStatus.Processing), (b, ScriptEditStatus.Queued));

        held.Release.SetResult();
        var terminal = new Dictionary<string, JsonObject>();
        while (terminal.Count < 2)
        {
            var frame = await talk.TerminalEditAsync();
            terminal.Add(frame["id"]!.GetValue<string>(), frame);
        }

        terminal[a]["version"]!.GetValue<int>().Should().Be(4);
        terminal[b]["version"]!.GetValue<int>().Should().Be(5);
        responses.Inputs.Should().HaveCount(2, "A's rewrite is reused on the revert (its target did not change)");
        responses.Inputs[1]["targets"]![0]!["narration"]!.GetValue<string>().Should().NotContain("Edit A text.");

        var rows = await TrainingDb.RowsAsync(postgres.ConnectionString, pid);
        rows.Select(r => (r.Number, r.Source, r.BaseVersion)).Should().Equal(
            (1, RevisionSources.Import, (int?)null), (2, RevisionSources.Import, 1), (3, RevisionSources.Revert, 2),
            (4, RevisionSources.LiveEdit, 3), (5, RevisionSources.LiveEdit, 4));
        var head = ScriptParser.Parse((await TrainingDb.HeadAsync(postgres.ConnectionString, pid)).Script, pid).Slides;
        var original = ScriptParser.Parse(v1, pid).Slides;
        head[0].Narration.Should().Be(original[0].Narration + " Edit A text.");
        head[1].Narration.Should().Be(original[1].Narration + " Edit B text.");
        head[2].Narration.Should().Be(original[2].Narration, "the revert is not overwritten");

        await talk.SendAsync("{\"type\":\"ping\"}");
        await talk.ReceiveUntilAsync(f => TalkClient.Type(f) == "pong");
        var edits = talk.Frames.Where(f => TalkClient.Type(f) == "script_edit").ToArray();
        foreach (var id in new[] { a, b })
            edits.Count(f => f["id"]!.GetValue<string>() == id
                && f["status"]!.GetValue<string>() is ScriptEditStatus.Applied or ScriptEditStatus.Failed).Should().Be(1);
        var versions = talk.Frames.Where(f => TalkClient.Type(f) == "script_version").Select(f => f["version"]!.GetValue<int>()).ToArray();
        versions.Should().BeInAscendingOrder().And.EndWith(5);
        await WaitForModelTextAsync(fake, "Edit A text.", 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task End_over_ws_while_the_commit_is_gated_after_cas(bool commitFirst)
    {
        var (owner, pid, _) = await TrainingDb.SeedAsync(postgres.ConnectionString);
        await using var fake = await FakeLiveServer.StartAsync();
        var responses = new StubResponsesHandler();
        var commits = new CommitGateInterceptor();
        await using var factory = TrainingFactory.Create(postgres, redis, fake.Url, responses, commits);
        using var talk = await TalkClient.ConnectAsync(factory, owner);
        await talk.StartAsync(pid);
        await talk.TrainerOnAsync();

        if (commitFirst)
        {
            // The worker holds the talk's commit lock inside the transaction (CAS done, COMMIT pending) when End arrives:
            // End waits for it, so the commit is durable and the next Start loads it.
            var gate = commits.Arm();
            await talk.TrainTurnAsync("Q?", "Committed before the end.", 0);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await talk.SendAsync("{\"type\":\"end\"}");
            await Task.Delay(300);
            talk.Frames.Should().NotContain(f => TalkClient.Type(f) == "closed");
            (await TrainingDb.HeadAsync(postgres.ConnectionString, pid)).Version.Should().Be(1, "COMMIT has not run yet");
            gate.Release.SetResult();
        }
        else
        {
            // End takes the lock first: the hung reviser call is cancelled and no commit of this talk ever starts.
            var held = responses.HoldNext();
            await talk.TrainTurnAsync("Q?", "Never committed.", 0);
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await talk.SendAsync("{\"type\":\"end\"}");
            await held.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await talk.ReceiveUntilAsync(f => TalkClient.Type(f) == "closed");
        var closedAt = talk.Frames.Count;
        await talk.ReceiveUntilAsync(f => TalkClient.Type(f) == "state" && f["state"]!.ToString() == "idle");
        talk.Frames.Skip(closedAt).Should().NotContain(f => TalkClient.Type(f) == "script_edit");

        var expected = commitFirst ? 2 : 1;
        (await TrainingDb.HeadAsync(postgres.ConnectionString, pid)).Version.Should().Be(expected);
        (await TrainingDb.RowsAsync(postgres.ConnectionString, pid)).Should().HaveCount(expected);
        (await talk.StartAsync(pid))["version"]!.GetValue<int>().Should().Be(expected);
    }

    [Fact]
    public async Task Commit_from_another_process_is_used_at_next_start()
    {
        var (owner, pid, v1) = await TrainingDb.SeedAsync(postgres.ConnectionString);
        await using var fake = await FakeLiveServer.StartAsync();
        await using var factory = TrainingFactory.Create(postgres, redis, fake.Url, new StubResponsesHandler());

        // While idle: another process commits v2; the Start loads it.
        var v2 = v1.Replace(TrainingDb.OpeningSentence, "Hello everyone, from another process.", StringComparison.Ordinal);
        (await TrainingDb.AppendAsync(postgres.ConnectionString, owner, pid, 1, v2, RevisionSources.Import))
            .Should().Be(new AppendResult.Applied(2));
        using var talk = await TalkClient.ConnectAsync(factory, owner);
        (await talk.StartAsync(pid))["version"]!.GetValue<int>().Should().Be(2);
        await WaitForModelTextAsync(fake, "from another process", 0);

        // During the talk: v3 lands elsewhere; this talk keeps v2 (no edit conflicted), the next Start presents v3.
        var v3 = v1.Replace(TrainingDb.OpeningSentence, "Hello everyone, third time lucky.", StringComparison.Ordinal);
        (await TrainingDb.AppendAsync(postgres.ConnectionString, owner, pid, 2, v3, RevisionSources.Revert))
            .Should().Be(new AppendResult.Applied(3));
        await talk.SendAsync("{\"type\":\"trainer_mode\",\"on\":true}");
        (await talk.ReceiveUntilAsync(f => TalkClient.Type(f) == "script_version" && f["trainerMode"]!.GetValue<bool>()))
            ["version"]!.GetValue<int>().Should().Be(2);
        factory.Services.GetRequiredService<IPresenter>().CurrentScriptVersion()!.Version.Should().Be(2);

        await talk.SendAsync("{\"type\":\"end\"}");
        await talk.ReceiveUntilAsync(f => TalkClient.Type(f) == "state" && f["state"]!.ToString() == "idle");
        var mark = fake.ReceivedSnapshot().Count;
        (await talk.StartAsync(pid))["version"]!.GetValue<int>().Should().Be(3);
        await WaitForModelTextAsync(fake, "third time lucky", mark);
    }

    [Fact]
    public async Task Remote_import_while_current_slide_is_held_rebuilds_and_reconciles()
    {
        var (owner, pid, v1) = await TrainingDb.SeedAsync(postgres.ConnectionString);
        await using var fake = await FakeLiveServer.StartAsync();
        var responses = new StubResponsesHandler();
        await using var factory = TrainingFactory.Create(postgres, redis, fake.Url, responses);
        using var talk = await TalkClient.ConnectAsync(factory, owner);
        await talk.StartAsync(pid);
        await talk.TrainerOnAsync();

        var held = responses.HoldNext();
        await talk.TrainTurnAsync("Q?", "Held slide gets this.", 0);
        await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // A CLI-style import commits v2 (it changes slide 3) while slide 1 is held on the pending edit.
        var imported = v1.Replace("Finally, the keyboard.", "Finally, the imported keyboard.", StringComparison.Ordinal);
        (await TrainingDb.AppendAsync(postgres.ConnectionString, owner, pid, 1, imported, RevisionSources.Import))
            .Should().Be(new AppendResult.Applied(2));

        // Release: the commit conflicts (expected 1, head 2), rebuilds on v2 and lands as v3.
        held.Release.SetResult();
        var applied = await talk.TerminalEditAsync();
        (applied["status"]!.GetValue<string>(), applied["version"]!.GetValue<int>()).Should().Be((ScriptEditStatus.Applied, 3));
        talk.Frames.Where(f => TalkClient.Type(f) == "script_version").Select(f => f["version"]!.GetValue<int>())
            .Should().Contain(3);
        responses.Inputs.Should().HaveCount(1, "slide 1 is unchanged in v2, so the rewrite is re-composed without a second call");
        await WaitForModelTextAsync(fake, "Held slide gets this.", 0);

        var (version, script) = await TrainingDb.HeadAsync(postgres.ConnectionString, pid);
        version.Should().Be(3);
        script.Should().Contain("Finally, the imported keyboard.").And.Contain("Held slide gets this.");
        (await TrainingDb.RowsAsync(postgres.ConnectionString, pid)).Select(r => r.Source).Should().Equal(
            RevisionSources.Import, RevisionSources.Import, RevisionSources.LiveEdit);

        // The presenter reconciled to v3 as a whole: slide 3 is narrated with the imported text.
        var mark = fake.ReceivedSnapshot().Count;
        await talk.SendAsync("{\"type\":\"goto\",\"index\":2}");
        await WaitForModelTextAsync(fake, "Finally, the imported keyboard.", mark);
    }

    private static async Task WaitForModelTextAsync(FakeLiveServer fake, string text, int fromIndex)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (fake.ReceivedSnapshot().Skip(fromIndex).Any(m => m.ToJsonString(Unescaped).Contains(text, StringComparison.Ordinal)))
                return;
            await Task.Delay(20);
        }

        throw new TimeoutException($"The model never received \"{text}\".");
    }

    private static readonly System.Text.Json.JsonSerializerOptions Unescaped = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
