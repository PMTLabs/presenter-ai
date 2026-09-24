using System.Collections.Concurrent;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Application.Scripts;
using PresenterAi.Application.Scripts.Revisions;
using PresenterAi.Infrastructure.Content;
using PresenterAi.Infrastructure.Persistence;
using PresenterAi.Integration.Tests.Support;
using Xunit;

namespace PresenterAi.Integration.Tests.Content;

/// <summary>
/// Plan 010 T6 (Postgres half, run in T11): the real <see cref="ScriptRevisionService"/> over the scoped
/// <see cref="PostgresPresentationRevisionStore"/>, registered as in production (one <c>DbContext</c> per scope), with a
/// fake reviser. A transaction interceptor pauses commits after the CAS and before <c>COMMIT</c>.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ScriptRevisionServicePostgresTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Two_presentations_edit_concurrently_on_postgres()
    {
        var first = await TrainingDb.SeedAsync(postgres.ConnectionString);
        var second = await TrainingDb.SeedAsync(postgres.ConnectionString);
        // Both transactions must be open at COMMIT at the same time before either proceeds: a DbContext shared by the
        // two workers would throw "a second operation was started" (or interleave the two transactions).
        var barrier = new CommitBarrier(2);
        await using var host = Host(barrier);
        var service = host.Service;
        service.OpenTalk("t1", first.OwnerId, first.PresentationId, CancellationToken.None);
        service.OpenTalk("t2", second.OwnerId, second.PresentationId, CancellationToken.None);

        var a = service.Enqueue("t1", Request(first, 0, "First presentation edit."));
        var b = service.Enqueue("t2", Request(second, 1, "Second presentation edit."));
        await barrier.AllArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));

        foreach (var (pid, id) in new[] { (first.PresentationId, a), (second.PresentationId, b) })
        {
            var outcome = await WaitTerminalAsync(service, pid, id);
            (outcome.Status, outcome.Version, outcome.Summary).Should().Be((ScriptEditStatus.Applied, 2, "Fake rewrite"));
        }

        host.Reviser.MaxConcurrent.Should().Be(2, "the two presentations have independent workers");

        foreach (var (seed, index, text) in new[] { (first, 0, "First presentation edit."), (second, 1, "Second presentation edit.") })
        {
            var (version, script) = await TrainingDb.HeadAsync(postgres.ConnectionString, seed.PresentationId);
            version.Should().Be(2);
            ScriptParser.Parse(script, seed.PresentationId).Slides[index].Narration.Should().EndWith(text);
            var rows = await TrainingDb.RowsAsync(postgres.ConnectionString, seed.PresentationId);
            rows.Select(r => (r.Number, r.Source)).Should().Equal((1, RevisionSources.Import), (2, RevisionSources.LiveEdit));
            rows[1].ChangedSlides.Should().Equal(index);
            rows[1].Script.Should().Be(script);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task End_between_cas_and_commit_on_postgres_in_both_orders(bool commitFirst)
    {
        var seed = await TrainingDb.SeedAsync(postgres.ConnectionString);
        var commits = new CommitGateInterceptor();
        await using var host = Host(commits);
        var service = host.Service;
        using var ticket = new CancellationTokenSource();
        var registration = service.OpenTalk("talk", seed.OwnerId, seed.PresentationId, ticket.Token);
        var commitGate = commits.Arm();
        // Outcomes of a closed talk are pruned at close, so record every state the Changed signal exposes.
        var seen = new ConcurrentQueue<EditOutcome>();
        service.Changed += pid =>
        {
            if (service.GetReconciliationSnapshot(pid, ["edit_1"]).Outcomes.TryGetValue("edit_1", out var state)) seen.Enqueue(state);
        };

        if (commitFirst)
        {
            // The worker is inside its commit transaction (UPDATE + INSERT done, COMMIT pending) and holds the lock.
            var id = service.Enqueue("talk", Request(seed, 0, "Committed first."));
            await commitGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            ticket.Cancel(); // End / max length off the loop: the ticket callback starts the close.
            var close = service.CloseTalkAsync("talk");
            await Task.Delay(300);
            close.IsCompleted.Should().BeFalse("the close waits for the in-flight commit");
            (await TrainingDb.HeadAsync(postgres.ConnectionString, seed.PresentationId)).Version.Should().Be(1);
            commitGate.Release.SetResult();
            await close.WaitAsync(TimeSpan.FromSeconds(10));
            id.Should().Be("edit_1");
            seen.Where(state => state.IsTerminal).Select(state => (state.Status, state.Version))
                .Should().Equal((ScriptEditStatus.Applied, (int?)2));
        }
        else
        {
            // The close holds the talk's commit lock when the worker reaches its commit phase: the close protocol
            // (acquire → MarkClosed → release) is played step by step so the worker is provably waiting on the lock
            // with a composed rewrite when the talk closes. It must then append nothing.
            var reviserGate = host.Reviser.HoldNext();
            var id = service.Enqueue("talk", Request(seed, 0, "Never committed."));
            await reviserGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            (await registration.CommitLock.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
            reviserGate.Release.SetResult();
            await Task.Delay(300);
            service.ActiveWorkerCount.Should().Be(1, "the worker waits for the lock in its commit phase");
            registration.MarkClosed().Should().BeTrue();
            registration.CommitLock.Release();
            ticket.Cancel();
            await service.CloseTalkAsync("talk").WaitAsync(TimeSpan.FromSeconds(10));
            id.Should().Be("edit_1");
            await WaitUntilAsync(() => service.ActiveWorkerCount == 0);
            seen.Should().NotContain(state => state.Status == ScriptEditStatus.Applied);
            commitGate.Entered.Task.IsCompleted.Should().BeFalse("no transaction of the closed talk reached COMMIT");
            commitGate.Release.SetResult();
        }

        var expected = commitFirst ? 2 : 1;
        (await TrainingDb.HeadAsync(postgres.ConnectionString, seed.PresentationId)).Version.Should().Be(expected);
        (await TrainingDb.RowsAsync(postgres.ConnectionString, seed.PresentationId)).Should().HaveCount(expected);
    }

    private static ScriptEditRequest Request((string OwnerId, string PresentationId, string Script) seed, int slide, string feedback) =>
        new(seed.PresentationId, seed.OwnerId, [slide], 1, feedback, null, []);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("Condition never became true.");
            await Task.Delay(20);
        }
    }

    private static async Task<EditOutcome> WaitTerminalAsync(ScriptRevisionService service, string presentationId, string id)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var outcome = service.GetReconciliationSnapshot(presentationId, [id]).Outcomes[id];
            if (outcome.IsTerminal) return outcome;
            await Task.Delay(20);
        }

        throw new TimeoutException($"{id} never reached a terminal outcome.");
    }

    private ServiceHost Host(IInterceptor interceptor)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddDbContext<PresenterAiDbContext>(options => options
                .UseNpgsql(postgres.ConnectionString, npgsql => npgsql.EnableRetryOnFailure())
                .AddInterceptors(interceptor))
            .AddScoped<IPresentationRevisionStore, PostgresPresentationRevisionStore>();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var reviser = new FakeReviser();
        return new ServiceHost(provider, reviser, new ScriptRevisionService(
            provider.GetRequiredService<IServiceScopeFactory>(), reviser, new TrainingOptions()));
    }

    private sealed class ServiceHost(ServiceProvider provider, FakeReviser reviser, ScriptRevisionService service) : IAsyncDisposable
    {
        public FakeReviser Reviser { get; } = reviser;

        public ScriptRevisionService Service { get; } = service;

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

    /// <summary>Appends the feedback to every target; a held call ignores cancellation (it returns only when released).</summary>
    private sealed class FakeReviser : IScriptReviser
    {
        private readonly ConcurrentQueue<Hold> _holds = new();
        private int _active;
        private int _maxConcurrent;

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public Hold HoldNext()
        {
            var hold = new Hold();
            _holds.Enqueue(hold);
            return hold;
        }

        public async Task<ScriptRevisionResult> ReviseAsync(ScriptRevisionRequest request, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            int seen;
            while ((seen = Volatile.Read(ref _maxConcurrent)) < active && Interlocked.CompareExchange(ref _maxConcurrent, active, seen) != seen)
            {
            }

            try
            {
                if (_holds.TryDequeue(out var hold))
                {
                    hold.Entered.TrySetResult();
                    await hold.Release.Task;
                }

                // Keep both first calls in flight together so concurrency is observable.
                await Task.Delay(100, CancellationToken.None);
                return new ScriptRevisionResult.Ok(
                    request.Targets.Select(target => new RevisedSlide(target.Number, target.Narration + " " + request.Feedback)).ToArray(),
                    "Fake rewrite");
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public sealed class Hold
        {
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Every commit waits at <c>COMMIT</c> until <paramref name="parties"/> transactions are waiting together.</summary>
    private sealed class CommitBarrier(int parties) : DbTransactionInterceptor
    {
        private int _arrived;

        public TaskCompletionSource AllArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _arrived) >= parties) AllArrived.TrySetResult();
            await AllArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            return result;
        }
    }
}
