using PresenterAi.Application.Presenting.Asking;

namespace PresenterAi.Application.Tests.Presenting.Asking;

/// <summary>
/// Plan 011 test harness for the <see cref="IAskTranscriber"/> port: records every ask it begins and the bytes
/// appended to it, and publishes updates from a thread-pool thread, as a real transcriber would.
/// </summary>
internal sealed class TestAskTranscriber : IAskTranscriber
{
    private readonly object _gate = new();
    private readonly List<TestAskTranscription> _transcriptions = [];

    /// <summary>When set, <see cref="Begin"/> throws it (the Ask start rollback path).</summary>
    public Exception? ThrowOnBegin { get; set; }

    public IReadOnlyList<TestAskTranscription> Transcriptions
    {
        get { lock (_gate) return [.. _transcriptions]; }
    }

    public TestAskTranscription? Current
    {
        get { lock (_gate) return _transcriptions.Count == 0 ? null : _transcriptions[^1]; }
    }

    public IAskTranscription? Begin(string askId)
    {
        if (ThrowOnBegin is not null) throw ThrowOnBegin;
        var transcription = new TestAskTranscription(askId);
        lock (_gate) _transcriptions.Add(transcription);
        return transcription;
    }

    /// <summary>Publishes an update on the current ask from a thread-pool thread; completes once raised.</summary>
    public Task PublishAsync(long revision, string text, bool final = false) =>
        (Current ?? throw new InvalidOperationException("no ask has begun")).PublishAsync(revision, text, final);
}

internal sealed class TestAskTranscription(string askId) : IAskTranscription
{
    private readonly object _gate = new();
    private readonly List<byte> _appended = [];
    private int _disposed;

    public string AskId { get; } = askId;

    public bool Disposed => Volatile.Read(ref _disposed) != 0;

    public int AppendCount { get; private set; }

    public byte[] Appended
    {
        get { lock (_gate) return [.. _appended]; }
    }

    public event Action<AskTranscriptUpdate>? Updated;

    public void Append(ReadOnlySpan<byte> pcm16)
    {
        lock (_gate)
        {
            _appended.AddRange(pcm16.ToArray());
            AppendCount++;
        }
    }

    /// <summary>Raises <see cref="Updated"/> from a thread-pool thread, even after disposal (a late update).</summary>
    public Task PublishAsync(long revision, string text, bool final = false) =>
        Task.Run(() => Updated?.Invoke(new AskTranscriptUpdate(revision, text, final)));

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
