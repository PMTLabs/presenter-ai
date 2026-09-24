using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PresenterAi.Application.Auth;
using PresenterAi.Application.Presenting;
using PresenterAi.Infrastructure.Persistence;
using PresenterAi.Infrastructure.Persistence.Entities;
using PresenterAi.Infrastructure.Sessions;
using PresenterAi.Infrastructure.Tests.Live;
using PresenterAi.Integration.Tests.Support;
using Xunit;

namespace PresenterAi.Integration.Tests.Sessions;

[Collection(IntegrationCollection.Name)]
public sealed class SessionRecorderTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task Recorder_closed_then_immediate_end_finalises_once()
    {
        var owner = await SeedPresentationAsync("direct-finalise");
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<PresenterAiDbContext>(options => options.UseNpgsql(postgres.ConnectionString))
            .BuildServiceProvider();
        var blockingScopes = new BlockingScopeFactory(services.GetRequiredService<IServiceScopeFactory>());
        await using var recorder = new SessionRecorder(
            blockingScopes,
            TimeProvider.System,
            NullLogger<SessionRecorder>.Instance);
        var presenter = new RecorderPresenter();
        var presentation = owner.Presentations.Single();
        recorder.Attach(presenter);
        presenter.RaiseTranscript("assistant", "before");
        presenter.RaiseTranscript("user", "after");
        var begin = recorder.BeginAsync(owner.Id, new PresenterStartResult(
            true, presentation.Id, "primary", "sess_direct", "model"));
        await blockingScopes.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        presenter.RaiseClosed("client_request", 12);
        var end = recorder.EndAsync("disconnect");
        blockingScopes.Gate.TrySetResult();
        await Task.WhenAll(begin, end);
        await WaitForAsync(() => Task.FromResult(blockingScopes.Count >= 3));
        await Task.Delay(100);
        blockingScopes.Count.Should().Be(3);

        await using var beforeDispose = CreateContext();
        var first = await beforeDispose.Sessions.SingleAsync(session => session.UserId == owner.Id);
        first.CloseReason.Should().Be("client_request");
        first.UsageSeconds.Should().Be(12);
        first.EndedAt.Should().NotBeNull();
        var id = first.Id;
        await Task.Delay(100);
        await recorder.DisposeAsync();

        await using var afterDispose = CreateContext();
        var settled = await afterDispose.Sessions.SingleAsync(session => session.Id == id);
        settled.CloseReason.Should().Be("client_request");
        settled.UsageSeconds.Should().Be(12);
    }

    [Fact]
    public async Task Begin_queued_before_immediate_end_still_creates_and_finalises_a_row()
    {
        var owner = await SeedPresentationAsync("direct-begin-end");
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<PresenterAiDbContext>(options => options.UseNpgsql(postgres.ConnectionString))
            .BuildServiceProvider();
        await using var recorder = new SessionRecorder(
            services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<SessionRecorder>.Instance);
        var presentation = owner.Presentations.Single();
        var begin = recorder.BeginAsync(owner.Id, new PresenterStartResult(
            true, presentation.Id, "primary", "sess_direct_begin", "model"));
        var end = recorder.EndAsync("disconnect");
        await Task.WhenAll(begin, end);

        await using var context = CreateContext();
        var sessions = await context.Sessions.Where(session => session.UserId == owner.Id).ToArrayAsync();
        sessions.Should().ContainSingle();
        sessions[0].EndedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Records_end_reason_local_times_and_confirmed_usage()
    {
        var owner = await SeedPresentationAsync("confirmed-usage");
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<PresenterAiDbContext>(options => options.UseNpgsql(postgres.ConnectionString))
            .BuildServiceProvider();
        var timeProvider = new FakeTimeProvider();
        await using var recorder = new SessionRecorder(
            services.GetRequiredService<IServiceScopeFactory>(),
            timeProvider,
            NullLogger<SessionRecorder>.Instance);
        var presenter = new RecorderPresenter();
        var presentation = owner.Presentations.Single();
        recorder.Attach(presenter);

        var startedAt = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);
        var endedAt = new DateTimeOffset(2026, 9, 23, 10, 15, 0, TimeSpan.Zero);

        await recorder.BeginAsync(owner.Id, new PresenterStartResult(
            true, presentation.Id, "primary", "sess_confirmed", "model", ConnectedAt: startedAt));

        var closed = new PresenterClosed(
            Reason: "client_request",
            Seconds: 15.0,
            EndReason: EndReasons.User,
            UsageConfirmed: true,
            EstimatedSeconds: 16.0,
            StartedAt: startedAt,
            EndedAt: endedAt);

        presenter.RaiseClosed(closed);
        await recorder.EndAsync("disconnect");

        await using var context = CreateContext();
        var session = await context.Sessions.SingleAsync(s => s.UserId == owner.Id);
        session.CloseReason.Should().Be("client_request");
        session.EndReason.Should().Be(EndReasons.User);
        session.UsageConfirmed.Should().BeTrue();
        session.EstimatedSeconds.Should().Be(16);
        session.UsageSeconds.Should().Be(15);
        session.StartedAt.Should().Be(startedAt);
        session.EndedAt.Should().Be(endedAt);
    }

    [Fact]
    public async Task Close_timeout_stores_the_estimated_seconds_not_zero()
    {
        var owner = await SeedPresentationAsync("close-timeout");
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<PresenterAiDbContext>(options => options.UseNpgsql(postgres.ConnectionString))
            .BuildServiceProvider();
        var timeProvider = new FakeTimeProvider();
        await using var recorder = new SessionRecorder(
            services.GetRequiredService<IServiceScopeFactory>(),
            timeProvider,
            NullLogger<SessionRecorder>.Instance);
        var presenter = new RecorderPresenter();
        var presentation = owner.Presentations.Single();
        recorder.Attach(presenter);

        var startedAt = new DateTimeOffset(2026, 9, 23, 11, 0, 0, TimeSpan.Zero);
        var endedAt = new DateTimeOffset(2026, 9, 23, 11, 5, 0, TimeSpan.Zero);

        await recorder.BeginAsync(owner.Id, new PresenterStartResult(
            true, presentation.Id, "primary", "sess_timeout", "model", ConnectedAt: startedAt));

        var closed = new PresenterClosed(
            Reason: "close_timeout",
            Seconds: null,
            EndReason: EndReasons.User,
            UsageConfirmed: false,
            EstimatedSeconds: 45.4,
            StartedAt: startedAt,
            EndedAt: endedAt);

        presenter.RaiseClosed(closed);
        await recorder.EndAsync("disconnect");

        await using var context = CreateContext();
        var session = await context.Sessions.SingleAsync(s => s.UserId == owner.Id);
        session.CloseReason.Should().Be("close_timeout");
        session.EndReason.Should().Be(EndReasons.User);
        session.UsageConfirmed.Should().BeFalse();
        session.EstimatedSeconds.Should().Be(45);
        session.UsageSeconds.Should().Be(45, "unconfirmed usage must store estimated seconds, not zero");
        session.StartedAt.Should().Be(startedAt);
        session.EndedAt.Should().Be(endedAt);
    }

    [Theory]
    [InlineData(0.2, 500, 1)]
    [InlineData(10, 200, 10)]
    public async Task Unconfirmed_usage_uses_elapsed_estimate_even_with_partial_seconds(
        double elapsed, double partialSeconds, int expected)
    {
        var owner = await SeedPresentationAsync("unconfirmed-estimate");
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<PresenterAiDbContext>(options => options.UseNpgsql(postgres.ConnectionString))
            .BuildServiceProvider();
        await using var recorder = new SessionRecorder(services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System, NullLogger<SessionRecorder>.Instance);
        var presenter = new RecorderPresenter();
        recorder.Attach(presenter);
        var started = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        await recorder.BeginAsync(owner.Id, new PresenterStartResult(true, owner.Presentations.Single().Id,
            "primary", "sess_estimate", "model", ConnectedAt: started));
        presenter.RaiseClosed(new PresenterClosed("close_timeout", partialSeconds, EndReasons.User,
            UsageConfirmed: false, EstimatedSeconds: 0, StartedAt: started,
            EndedAt: started.AddSeconds(elapsed)));
        await recorder.EndAsync("disconnect");
        await using var context = CreateContext();
        var row = await context.Sessions.SingleAsync(s => s.UserId == owner.Id);
        row.UsageSeconds.Should().Be(expected);
        row.EstimatedSeconds.Should().Be(expected);
        row.UsageConfirmed.Should().BeFalse();
    }

    [Fact]
    public async Task Heartbeat_abort_persists_heartbeat_end_reason()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        var owner = await SeedPresentationAsync("heartbeat-row");
        var clock = new FakeTimeProvider();
        await using var factory = new IntegrationApiFactory(postgres, redis, fake.Url) { Clock = clock };
        using var socket = await ConnectAsync(factory, owner.Id);
        await StartAsync(socket, owner.Presentations.Single().Id);
        clock.Advance(TimeSpan.FromSeconds(45));
        await WaitForAsync(async () => await SessionFinalisedAsync(owner.Id, expected: 1));
        var row = await ReadSingleSessionAsync(owner.Id);
        row.EndReason.Should().Be(EndReasons.Heartbeat);
        // Abort closes the transport before its writer can deliver a closed frame.
    }

    [Fact]
    public async Task Bridge_end_without_closed_stores_disconnect_and_unconfirmed()
    {
        var owner = await SeedPresentationAsync("bridge-end-no-closed");
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<PresenterAiDbContext>(options => options.UseNpgsql(postgres.ConnectionString))
            .BuildServiceProvider();
        var timeProvider = new FakeTimeProvider();
        var startTime = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        timeProvider.SetUtcNow(startTime);

        await using var recorder = new SessionRecorder(
            services.GetRequiredService<IServiceScopeFactory>(),
            timeProvider,
            NullLogger<SessionRecorder>.Instance);
        var presenter = new RecorderPresenter();
        var presentation = owner.Presentations.Single();
        recorder.Attach(presenter);

        await recorder.BeginAsync(owner.Id, new PresenterStartResult(
            true, presentation.Id, "primary", "sess_no_closed", "model"));

        timeProvider.Advance(TimeSpan.FromSeconds(25));

        await recorder.EndAsync("disconnect");

        await using var context = CreateContext();
        var session = await context.Sessions.SingleAsync(s => s.UserId == owner.Id);
        session.CloseReason.Should().Be("disconnect");
        session.EndReason.Should().Be(EndReasons.Disconnect);
        session.UsageConfirmed.Should().BeFalse();
        session.EstimatedSeconds.Should().Be(25);
        session.UsageSeconds.Should().Be(25);
        session.StartedAt.Should().Be(startTime);
        session.EndedAt.Should().Be(startTime + TimeSpan.FromSeconds(25));
    }

    [Fact]
    public async Task Completed_run_records_metadata_and_ordered_turns()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        var owner = await SeedPresentationAsync("completed");
        await using var factory = new IntegrationApiFactory(postgres, redis, fake.Url);
        using var socket = await ConnectAsync(factory, owner.Id);

        await StartAsync(socket, owner.Presentations.Single().Id);
        await SendAsync(socket, "{\"type\":\"end\"}");
        await ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "closed");
        await WaitForAsync(async () => await SessionFinalisedAsync(owner.Id, expected: 1));

        await using var context = CreateContext();
        var session = await context.Sessions.Include(item => item.Turns).SingleAsync(item => item.UserId == owner.Id);
        session.StartedAt.Should().NotBe(default);
        session.EndedAt.Should().NotBeNull();
        session.UsageSeconds.Should().Be(7);
        session.Upstream.Should().Be("primary");
        session.UpstreamSessionId.Should().Be("sess_fake");
        session.CloseReason.Should().Be("client_request");
        session.Turns.Should().BeInAscendingOrder(turn => turn.Ordinal);
        session.Turns.Should().NotBeEmpty();
        session.Turns.Should().OnlyContain(turn => turn.SlideNo == 1);
    }

    [Fact]
    public async Task A_disconnect_still_finalises_the_row()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        var owner = await SeedPresentationAsync("disconnect");
        await using var factory = new IntegrationApiFactory(postgres, redis, fake.Url);
        using var first = await ConnectAsync(factory, owner.Id);
        await StartAsync(first, owner.Presentations.Single().Id);
        await first.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "disconnect", CancellationToken.None);

        // A state frame proves that the bridge released its slot. D-f requires the row to be complete before that.
        using var next = await ConnectAsync(factory, owner.Id);
        var session = await ReadSingleSessionAsync(owner.Id);
        session.EndedAt.Should().NotBeNull();
        session.CloseReason.Should().Be("client_request");
    }

    [Fact]
    public async Task Failed_start_leaves_no_row()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        var owner = await SeedPresentationAsync("failed-start");
        await using var factory = new IntegrationApiFactory(postgres, redis, fake.Url, primaryModel: "bad-model");
        using var socket = await ConnectAsync(factory, owner.Id);
        await SendAsync(socket, $"{{\"type\":\"start\",\"presentation\":\"{owner.Presentations.Single().Id}\"}}");
        // The bridge sends this error only after ObserveStartAsync has retired the recorder and awaited its
        // EndAsync attempt, so it is the deterministic failed-start barrier before the negative assertion.
        await ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "error");
        (await SessionCountAsync(owner.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Fallback_records_the_selected_route_and_session()
    {
        await using var primary = await FakeLiveServer.StartAsync();
        await using var fallback = await FakeLiveServer.StartAsync(sessionId: "sess_fallback");
        var owner = await SeedPresentationAsync("fallback");
        await using var factory = new IntegrationApiFactory(
            postgres, redis, primary.Url, fallback.Url, primaryModel: "bad-model");
        using var socket = await ConnectAsync(factory, owner.Id);
        await StartAsync(socket, owner.Presentations.Single().Id);
        await SendAsync(socket, "{\"type\":\"end\"}");
        await ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "closed");
        await WaitForAsync(async () => await SessionFinalisedAsync(owner.Id, expected: 1));

        var session = await ReadSingleSessionAsync(owner.Id);
        session.Upstream.Should().Be("fallback");
        session.UpstreamSessionId.Should().Be("sess_fallback");
    }

    [Fact]
    public async Task Two_sequential_runs_leave_two_independent_rows()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        var owner = await SeedPresentationAsync("sequential");
        await using var factory = new IntegrationApiFactory(postgres, redis, fake.Url);
        using var socket = await ConnectAsync(factory, owner.Id);

        await StartAsync(socket, owner.Presentations.Single().Id);
        await SendAsync(socket, "{\"type\":\"end\"}");
        await ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "closed");
        await ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "state" && frame["state"]?.GetValue<string>() == "idle");
        await WaitForAsync(async () => await SessionFinalisedAsync(owner.Id, expected: 1));
        var first = await ReadSingleSessionAsync(owner.Id);

        await StartAsync(socket, owner.Presentations.Single().Id);
        await SendAsync(socket, "{\"type\":\"end\"}");
        await ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "closed");
        await WaitForAsync(async () => await SessionFinalisedAsync(owner.Id, expected: 2));

        await using var context = CreateContext();
        var sessions = await context.Sessions.Where(item => item.UserId == owner.Id).OrderBy(item => item.StartedAt).ToArrayAsync();
        sessions.Should().HaveCount(2);
        sessions[0].Id.Should().Be(first.Id);
        sessions[0].Upstream.Should().Be(first.Upstream);
        sessions[0].EndedAt.Should().Be(first.EndedAt);
        sessions[1].Id.Should().NotBe(first.Id);
    }

    private async Task<User> SeedPresentationAsync(string slug)
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var owner = new User
        {
            Email = $"{Guid.NewGuid():N}@session.test",
            DisplayName = "Session owner",
            AuthMethod = "test",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var markdown = await File.ReadAllTextAsync(Path.Combine(FindRepositoryRoot(), "presentations", "sample.md"));
        var parsed = PresenterAi.Application.Scripts.ScriptParser.Parse(markdown, slug);
        var presentation = new Presentation
        {
            OwnerId = owner.Id,
            Slug = "session-" + slug,
            Title = parsed.Meta.Title,
            Deck = parsed.Meta.Deck,
            Driver = parsed.Meta.Driver,
            Script = markdown,
            SlideCount = parsed.Slides.Count,
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        owner.Presentations.Add(presentation);
        context.Users.Add(owner);
        await context.SaveChangesAsync();
        return owner;
    }

    private async Task<WebSocket> ConnectAsync(IntegrationApiFactory factory, string userId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var ticket = Guid.NewGuid().ToString("N");
            await factory.Services.GetRequiredService<ITicketStore>().IssueAsync(ticket, userId);
            var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
            await SendAsync(socket, $"{{\"type\":\"auth\",\"ticket\":\"{ticket}\"}}");
            try
            {
                await ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "state");
                return socket;
            }
            catch (InvalidOperationException)
            {
                socket.Dispose();
                await Task.Delay(20);
            }
        }

        throw new TimeoutException("The bridge slot did not become available.");
    }

    private static async Task StartAsync(WebSocket socket, string presentationId)
    {
        await SendAsync(socket, $"{{\"type\":\"start\",\"presentation\":\"{presentationId}\"}}");
        await ReceiveUntilAsync(socket, frame => frame["type"]?.GetValue<string>() == "state" && frame["state"]?.GetValue<string>() == "presenting");
    }

    private static async Task SendAsync(WebSocket socket, string value) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(value), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonObject> ReceiveUntilAsync(WebSocket socket, Func<JsonObject, bool> predicate)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var buffer = new byte[32 * 1024];
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("The WebSocket closed before the expected frame.");
            if (result.MessageType == WebSocketMessageType.Binary)
                continue;
            var frame = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count))!.AsObject();
            if (predicate(frame)) return frame;
        }

        throw new TimeoutException("Expected WebSocket frame was not received.");
    }

    private async Task<int> SessionCountAsync(string userId)
    {
        await using var context = CreateContext();
        return await context.Sessions.CountAsync(session => session.UserId == userId);
    }

    private async Task<bool> SessionFinalisedAsync(string userId, int expected)
    {
        await using var context = CreateContext();
        return await context.Sessions.CountAsync(session => session.UserId == userId && session.EndedAt != null) == expected;
    }

    private async Task<Session> ReadSingleSessionAsync(string userId)
    {
        await using var context = CreateContext();
        return await context.Sessions.SingleAsync(session => session.UserId == userId);
    }

    private PresenterAiDbContext CreateContext() => new(new DbContextOptionsBuilder<PresenterAiDbContext>()
        .UseNpgsql(postgres.ConnectionString)
        .Options);

    private sealed class BlockingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        private int _count;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count => Volatile.Read(ref _count);

        public IServiceScope CreateScope()
        {
            var scope = inner.CreateScope();
            if (Interlocked.Increment(ref _count) == 2)
            {
                Entered.TrySetResult();
                Gate.Task.GetAwaiter().GetResult();
            }

            return scope;
        }
    }

    private sealed class RecorderPresenter : IPresenter
    {
        private Action<PresenterClosed>? _closed;
        private Action<PresenterTranscript>? _transcript;

        public event Action<PresenterSnapshot>? State { add { } remove { } }
        public event Action<int>? Slide { add { } remove { } }
        public event Action<PresenterAudio>? Audio { add { } remove { } }
        public event Action<PresenterTranscript>? Transcript { add => _transcript += value; remove => _transcript -= value; }
        public event Action<PresenterUsage>? Usage { add { } remove { } }
        public event Action<PresenterClosed>? Closed { add => _closed += value; remove => _closed -= value; }
        public event Action<PresenterLog>? Log { add { } remove { } }
        public event Action<PresenterUpstreamError>? UpstreamError { add { } remove { } }

        public PresenterSnapshot Snapshot() => new("presenting", null, null, 0, 1, false, false, null, null, 0, 200);
        public void RaiseTranscript(string role, string delta) => _transcript?.Invoke(new PresenterTranscript(role, delta, null, null));
        public void RaiseClosed(PresenterClosed closed) => _closed?.Invoke(closed);
        public void RaiseClosed(string reason, double seconds) => _closed?.Invoke(new PresenterClosed(reason, seconds, UsageConfirmed: true));
        public Task<PresenterStartResult> StartAsync(string id, int? fromIndex, string ownerId, CancellationToken cancellationToken = default) => Task.FromResult(new PresenterStartResult(false, id, null, null, null));
        public Task<bool> NextAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> PrevAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> GotoAsync(int index, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> PauseAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> ResumeAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> MuteAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> UnmuteAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> EndAsync(bool resumable = false, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

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
