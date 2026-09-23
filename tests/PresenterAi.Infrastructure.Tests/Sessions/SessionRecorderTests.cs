using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PresenterAi.Application.Presenting;
using PresenterAi.Infrastructure.Sessions;
using Xunit;

namespace PresenterAi.Infrastructure.Tests.Sessions;

public sealed class SessionRecorderTests
{
    [Fact]
    public async Task Attach_and_detach_manage_all_four_recorder_handlers()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var recorder = new SessionRecorder(
            services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<SessionRecorder>.Instance);
        var presenter = new CountingPresenter();

        recorder.Attach(presenter);
        presenter.SubscriberCount.Should().Be(4);

        recorder.Detach();
        presenter.SubscriberCount.Should().Be(0);
        recorder.Detach();
        presenter.SubscriberCount.Should().Be(0);

        await recorder.DisposeAsync();
        presenter.SubscriberCount.Should().Be(0);
    }

    private sealed class CountingPresenter : IPresenter
    {
        private Action<int>? _slide;
        private Action<PresenterTranscript>? _transcript;
        private Action<PresenterUsage>? _usage;
        private Action<PresenterClosed>? _closed;

        public int SubscriberCount =>
            (_slide?.GetInvocationList().Length ?? 0)
            + (_transcript?.GetInvocationList().Length ?? 0)
            + (_usage?.GetInvocationList().Length ?? 0)
            + (_closed?.GetInvocationList().Length ?? 0);

        public event Action<PresenterSnapshot>? State { add { } remove { } }
        public event Action<int>? Slide { add => _slide += value; remove => _slide -= value; }
        public event Action<PresenterAudio>? Audio { add { } remove { } }
        public event Action<PresenterTranscript>? Transcript { add => _transcript += value; remove => _transcript -= value; }
        public event Action<PresenterUsage>? Usage { add => _usage += value; remove => _usage -= value; }
        public event Action<PresenterClosed>? Closed { add => _closed += value; remove => _closed -= value; }
        public event Action<PresenterLog>? Log { add { } remove { } }
        public event Action<PresenterUpstreamError>? UpstreamError { add { } remove { } }

        public PresenterSnapshot Snapshot() => new("idle", null, null, 0, 0, false, false, null, null, 0, 0);
        public Task<PresenterStartResult> StartAsync(string id, int? fromIndex, string ownerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PresenterStartResult(false, id, null, null, null));
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
}
