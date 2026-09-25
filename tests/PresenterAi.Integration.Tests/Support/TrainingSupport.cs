using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Application.Auth;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Scripts.Revisions;
using PresenterAi.Infrastructure.Content;
using PresenterAi.Infrastructure.Persistence;
using PresenterAi.Infrastructure.Persistence.Entities;
using PresenterAi.Infrastructure.Training;

namespace PresenterAi.Integration.Tests.Support;

/// <summary>
/// Plan 010 T11: the only fakes of the end-to-end flows are the upstreams — the GPT-Live socket (FakeLiveServer) and
/// the Responses endpoint (this handler, installed as the primary handler of the named <c>"responses"</c> client, so
/// the real <see cref="ResponsesScriptReviser"/> builds and parses every request).
/// </summary>
public sealed class StubResponsesHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<JsonObject> _inputs = new();
    private readonly ConcurrentQueue<Gate> _holds = new();

    /// <summary>New narration for one target (<c>{number,title,narration,notes}</c>) of a request's <c>input</c>.</summary>
    /// <remarks>Default: the target's narration plus the selected answer ("Train on this") or else the feedback.</remarks>
    public Func<JsonObject, JsonObject, string> Rewrite { get; set; } =
        (target, input) => target["narration"]!.GetValue<string>() + " "
            + (input["request"]!["exchange"]?["answer"] ?? input["request"]!["feedback"])!.GetValue<string>();

    /// <summary>Every request's parsed <c>input</c> (the reviser sends it as JSON text), in arrival order.</summary>
    public IReadOnlyList<JsonObject> Inputs => _inputs.ToArray();

    /// <summary>The next request waits for <see cref="Gate.Release"/> (or its cancellation).</summary>
    public Gate HoldNext()
    {
        var gate = new Gate();
        _holds.Enqueue(gate);
        return gate;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        var input = JsonNode.Parse(body["input"]!.GetValue<string>())!.AsObject();
        _inputs.Enqueue(input);
        if (_holds.TryDequeue(out var gate))
        {
            gate.Entered.TrySetResult();
            try
            {
                await gate.Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                gate.Cancelled.TrySetResult();
                throw;
            }
        }

        var slides = new JsonArray(input["targets"]!.AsArray()
            .Select(target => (JsonNode)new JsonObject
            {
                ["number"] = target!["number"]!.GetValue<int>(),
                ["narration"] = Rewrite(target.AsObject(), input)
            })
            .ToArray());
        var text = new JsonObject { ["slides"] = slides, ["summary"] = "Stub rewrite" }.ToJsonString();
        var response = new JsonObject
        {
            ["status"] = "completed",
            ["output"] = new JsonArray(new JsonObject
            {
                ["type"] = "message",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text })
            }),
            ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 20 }
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json")
        };
    }

    public sealed class Gate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>
/// Test seam for the End-vs-commit linearization on Postgres: when armed, the next transaction commit of a context
/// that carries this interceptor pauses after its statements (the version CAS <c>UPDATE</c> and the revision
/// <c>INSERT</c>) and before <c>COMMIT</c>, until released. Test-only; production code has no hook.
/// </summary>
public sealed class CommitGateInterceptor : DbTransactionInterceptor
{
    private Gate? _armed;

    public Gate Arm()
    {
        var gate = new Gate();
        Volatile.Write(ref _armed, gate);
        return gate;
    }

    public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        var gate = Interlocked.Exchange(ref _armed, null);
        if (gate is not null)
        {
            // The row is updated and inserted but not yet committed: another connection still reads the old head.
            gate.Entered.TrySetResult();
            await gate.Release.Task.ConfigureAwait(false);
        }

        return result;
    }

    public sealed class Gate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>Seeding and reading presentations and revisions directly, as "another process" would.</summary>
public static class TrainingDb
{
    public const string OpeningSentence = "Hello everyone, and welcome.";

    public static async Task<(string OwnerId, string PresentationId, string Script)> SeedAsync(string connectionString)
    {
        await using var context = CreateContext(connectionString);
        await context.Database.MigrateAsync();
        var owner = new User
        {
            Email = $"{Guid.NewGuid():N}@training.test",
            DisplayName = "Training owner",
            AuthMethod = "test",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var script = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "presentations", "sample.md"));
        var presentation = new Presentation
        {
            OwnerId = owner.Id,
            Slug = "training-" + Guid.NewGuid().ToString("N"),
            Title = "Presenter-AI sample",
            Deck = "decks/sample/index.html",
            Driver = "auto",
            Script = script,
            SlideCount = 3,
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        owner.Presentations.Add(presentation);
        context.Users.Add(owner);
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
        return (owner.Id, presentation.Id, script);
    }

    /// <summary>A commit by another writer (the CLI import, another API process) through its own context.</summary>
    public static async Task<AppendResult> AppendAsync(
        string connectionString, string ownerId, string presentationId, int expected, string script, string source,
        string summary = "Changed elsewhere")
    {
        await using var context = CreateContext(connectionString);
        var head = await new PostgresPresentationRevisionStore(context, TimeProvider.System)
            .GetHeadAsync(ownerId, presentationId);
        var target = PresenterAi.Application.Scripts.ScriptParser.Parse(script, presentationId);
        var changed = head!.Slides.Where((slide, index) => slide.Narration != target.Slides[index].Narration)
            .Select(slide => slide.Index).ToArray();
        return await new PostgresPresentationRevisionStore(context, TimeProvider.System).TryAppendAsync(
            ownerId, presentationId, expected,
            new NewRevision(script, source, summary, expected, null, changed, null));
    }

    public static async Task<(int Version, string Script)> HeadAsync(string connectionString, string presentationId)
    {
        await using var context = CreateContext(connectionString);
        var row = await context.Presentations.AsNoTracking().SingleAsync(item => item.Id == presentationId);
        return (row.Version, row.Script);
    }

    public static async Task<PresentationRevision[]> RowsAsync(string connectionString, string presentationId)
    {
        await using var context = CreateContext(connectionString);
        return await context.PresentationRevisions.AsNoTracking()
            .Where(row => row.PresentationId == presentationId)
            .OrderBy(row => row.Number)
            .ToArrayAsync();
    }

    public static PresenterAiDbContext CreateContext(string connectionString) => new(
        new DbContextOptionsBuilder<PresenterAiDbContext>().UseNpgsql(connectionString).Options);

    public static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "PresenterAi.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("PresenterAi.slnx was not found above the test assembly.");
    }
}

/// <summary>A <c>/ws</c> client that keeps every text frame it has read, in order.</summary>
public sealed class TalkClient : IDisposable
{
    private readonly WebSocket _socket;

    private TalkClient(WebSocket socket) => _socket = socket;

    public List<JsonObject> Frames { get; } = [];

    public static async Task<TalkClient> ConnectAsync(IntegrationApiFactory factory, string userId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var ticket = Guid.NewGuid().ToString("N");
            await factory.Services.GetRequiredService<ITicketStore>().IssueAsync(ticket, userId);
            var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
            var client = new TalkClient(socket);
            await client.SendAsync($"{{\"type\":\"auth\",\"ticket\":\"{ticket}\"}}");
            try
            {
                var first = await client.ReceiveUntilAsync(frame => Type(frame) is "state" or "error");
                if (Type(first) == "state") return client;
            }
            catch (InvalidOperationException)
            {
            }

            client.Dispose();
            await Task.Delay(20);
        }

        throw new TimeoutException("The bridge slot did not become available.");
    }

    public Task SendAsync(string text) =>
        _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>Starts the talk and returns its <c>script_version</c> frame (sent after the first slide of a Start).</summary>
    public async Task<JsonObject> StartAsync(string presentationId)
    {
        await SendAsync($"{{\"type\":\"start\",\"presentation\":\"{presentationId}\"}}");
        return await ReceiveUntilAsync(frame => Type(frame) == "script_version");
    }

    public async Task TrainerOnAsync()
    {
        await SendAsync("{\"type\":\"trainer_mode\",\"on\":true}");
        await ReceiveUntilAsync(frame => Type(frame) == "script_version" && frame["trainerMode"]!.GetValue<bool>());
    }

    public Task TrainTurnAsync(string question, string answer, int slideIndex) =>
        SendAsync(new JsonObject
        {
            ["type"] = "train_turn", ["question"] = question, ["answer"] = answer, ["slideIndex"] = slideIndex
        }.ToJsonString());

    public Task<JsonObject> TerminalEditAsync(string? id = null) =>
        ReceiveUntilAsync(frame => Type(frame) == "script_edit"
            && (id is null || frame["id"]!.GetValue<string>() == id)
            && frame["status"]!.GetValue<string>() is ScriptEditStatus.Applied or ScriptEditStatus.Failed);

    public async Task<JsonObject> ReceiveUntilAsync(Func<JsonObject, bool> predicate, int seconds = 10)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var buffer = new byte[64 * 1024];
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(seconds));
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException("The WebSocket closed before the expected frame.");
                stream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);
            if (result.MessageType == WebSocketMessageType.Binary) continue;
            var frame = JsonNode.Parse(Encoding.UTF8.GetString(stream.ToArray()))!.AsObject();
            Frames.Add(frame);
            if (predicate(frame)) return frame;
        }

        throw new TimeoutException("Expected WebSocket frame was not received.");
    }

    public static string? Type(JsonObject frame) => frame["type"]?.GetValue<string>();

    public void Dispose() => _socket.Dispose();
}

public static class TrainingFactory
{
    /// <summary>
    /// The real API (Postgres store, revision service, Responses reviser, presenter, bridge) with only the upstreams
    /// faked. A fake clock (at the real time, so tokens stay valid) keeps the talk on slide 1 until a test moves it.
    /// </summary>
    public static IntegrationApiFactory Create(
        PostgresFixture postgres,
        RedisFixture redis,
        string liveUrl,
        StubResponsesHandler responses,
        CommitGateInterceptor? commitGate = null)
    {
        var factory = new IntegrationApiFactory(postgres, redis, liveUrl)
        {
            Clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow),
            TestServices = services =>
            {
                services.AddHttpClient(ResponsesScriptReviser.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => responses);
                if (commitGate is not null)
                    services.ConfigureDbContext<PresenterAiDbContext>(options => options.AddInterceptors(commitGate));
            }
        };
        factory.Settings["Upstream:DelegationModel"] = "managed";
        return factory;
    }

    public static async Task SettleAsync(this IntegrationApiFactory factory, TimeSpan advance = default)
    {
        if (advance > TimeSpan.Zero) factory.Clock!.Advance(advance);
        if (factory.Services.GetRequiredService<IPresenter>() is Presenter presenter)
            await presenter.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
