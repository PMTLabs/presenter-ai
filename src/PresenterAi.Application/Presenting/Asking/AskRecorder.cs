namespace PresenterAi.Application.Presenting.Asking;

/// <summary>
/// Plan 011 §4.1: an online, energy-based silence compressor for the audio of one ask (24 kHz mono PCM16).
/// It classifies 20 ms windows by RMS, keeps pauses up to 500 ms verbatim, squeezes longer pauses to a head and a
/// tail of 160 ms each, trims leading and trailing silence to 160 ms, caps the retained audio at 25 s of kept speech (P-1: an unpaced
/// burst is ingested upstream only up to about 30 s; silence squeezed out does not count) and, on
/// <see cref="Complete"/>, appends a zero tail and cuts the result into 200 ms chunks. Pure and single-threaded: the
/// presenter loop owns it. No audio content ever leaves it except through <see cref="Complete"/>.
/// </summary>
public sealed class AskRecorder
{
    public const int BytesPerMs = 48;
    public const int WindowMs = 20;
    public const int WindowBytes = WindowMs * BytesPerMs;
    public const int VoiceRms = AudioLevel.VoiceThreshold;
    public const int MaxVerbatimGapMs = 500;
    public const int DefaultGapKeepMs = 320;
    public const int LeadMs = 160;
    public const int TrailMs = 160;
    public const int DefaultTailSilenceMs = 1_000;
    public const int MinSpeechMs = 200;
    public const int MaxRetainedMs = 25_000;
    public const int ChunkMs = 200;
    public const int ChunkBytes = ChunkMs * BytesPerMs;

    /// <summary>T1 may retune the zero tail within these bounds (§4.1).</summary>
    public const int MinTailSilenceMs = 500;
    public const int MaxTailSilenceMs = 2_000;

    /// <summary>T1 may retune the kept gap within these bounds (§4.1).</summary>
    public const int MinGapKeepMs = 200;
    public const int MaxGapKeepMs = 400;

    private const int VerbatimGapWindows = MaxVerbatimGapMs / WindowMs;
    private const int LeadWindows = LeadMs / WindowMs;
    private const int TrailWindows = TrailMs / WindowMs;
    private const int MaxRetainedBytes = MaxRetainedMs * BytesPerMs;

    private readonly bool _compress;
    private readonly int _gapHeadWindows;
    private readonly int _gapTailWindows;
    private readonly byte[] _carry = new byte[WindowBytes];
    private readonly List<byte[]> _pending = [];
    private readonly MemoryStream _kept = new();
    private int _carryLength;
    private int _gapWindows;
    private bool _started;
    private bool _completed;
    private long _recordedWindows;
    private long _voicedWindows;
    private long? _lastVoicedAtMs;
    private readonly long[] _bands = new long[5];

    public AskRecorder(int tailSilenceMs = DefaultTailSilenceMs, int gapKeepMs = DefaultGapKeepMs, bool compress = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tailSilenceMs, MinTailSilenceMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tailSilenceMs, MaxTailSilenceMs);
        ArgumentOutOfRangeException.ThrowIfLessThan(gapKeepMs, MinGapKeepMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(gapKeepMs, MaxGapKeepMs);

        TailSilenceMs = tailSilenceMs;
        _compress = compress;
        var keepWindows = gapKeepMs / WindowMs;
        _gapHeadWindows = keepWindows / 2;
        _gapTailWindows = keepWindows - _gapHeadWindows;
    }

    public int TailSilenceMs { get; }

    /// <summary>True once the retained (post-compression) audio reached <see cref="MaxRetainedMs"/>; later audio is ignored.</summary>
    public bool IsFull { get; private set; }

    /// <summary>At least <see cref="MinSpeechMs"/> of voiced windows were heard.</summary>
    public bool HasSpeech => _voicedWindows * WindowMs >= MinSpeechMs;

    public AskRecorderStats Stats => new(
        _recordedWindows * WindowMs,
        _kept.Length / BytesPerMs,
        _voicedWindows * WindowMs,
        _lastVoicedAtMs,
        new AskRmsBands(_bands[0] * WindowMs, _bands[1] * WindowMs, _bands[2] * WindowMs, _bands[3] * WindowMs, _bands[4] * WindowMs),
        _completed ? TailSilenceMs : 0,
        IsFull);

    /// <summary>
    /// Appends mic PCM16. Bytes that do not fill a 20 ms window (odd bytes included) carry over to the next call.
    /// Returns <see cref="IsFull"/>.
    /// </summary>
    public bool Append(ReadOnlySpan<byte> pcm16)
    {
        if (_completed)
        {
            throw new InvalidOperationException("AskRecorder: Append after Complete");
        }

        while (!pcm16.IsEmpty && !IsFull)
        {
            var take = Math.Min(WindowBytes - _carryLength, pcm16.Length);
            pcm16[..take].CopyTo(_carry.AsSpan(_carryLength));
            _carryLength += take;
            pcm16 = pcm16[take..];
            if (_carryLength == WindowBytes)
            {
                _carryLength = 0;
                OnWindow(_carry.ToArray());
            }
        }

        return IsFull;
    }

    /// <summary>
    /// Ends the recording: keeps up to 160 ms of trailing silence, appends the zero tail and returns the stream as
    /// 200 ms chunks (the last one may be shorter). A partial window still in the carry is dropped.
    /// </summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> Complete()
    {
        if (_completed)
        {
            throw new InvalidOperationException("AskRecorder: Complete called twice");
        }

        _completed = true;
        if (_compress && _started)
        {
            // The windows right after the last voice: a compressed gap holds them in its head only.
            var trail = _gapWindows > VerbatimGapWindows ? Math.Min(TrailWindows, _gapHeadWindows) : TrailWindows;
            foreach (var window in _pending.Take(trail))
            {
                Commit(window);
            }
        }

        _pending.Clear();
        var stream = new byte[_kept.Length + (long)TailSilenceMs * BytesPerMs];
        _kept.Position = 0;
        _kept.ReadExactly(stream, 0, (int)_kept.Length);

        var chunks = new List<ReadOnlyMemory<byte>>((stream.Length + ChunkBytes - 1) / ChunkBytes);
        for (var offset = 0; offset < stream.Length; offset += ChunkBytes)
        {
            chunks.Add(stream.AsMemory(offset, Math.Min(ChunkBytes, stream.Length - offset)));
        }

        return chunks;
    }

    private void OnWindow(byte[] window)
    {
        _recordedWindows++;
        var rms = AudioLevel.Rms(window, stride: 1);
        _bands[rms switch { < 30 => 0, < VoiceRms => 1, < 500 => 2, < 2_000 => 3, _ => 4 }]++;
        var voiced = rms >= VoiceRms;
        if (voiced)
        {
            _voicedWindows++;
            _lastVoicedAtMs = _recordedWindows * WindowMs;
        }

        if (!_compress)
        {
            _started |= voiced;
            Commit(window);
            return;
        }

        if (!voiced)
        {
            _pending.Add(window);
            _gapWindows++;
            if (!_started)
            {
                // Lead-in: only a rolling ring of the last 160 ms before the first voice is kept.
                if (_pending.Count > LeadWindows)
                {
                    _pending.RemoveAt(0);
                }
            }
            else if (_gapWindows > VerbatimGapWindows)
            {
                // A thinking pause: keep its first windows and a rolling ring of its last ones.
                while (_pending.Count > _gapHeadWindows + _gapTailWindows)
                {
                    _pending.RemoveAt(_gapHeadWindows);
                }
            }

            return;
        }

        _started = true;
        foreach (var quiet in _pending)
        {
            Commit(quiet);
        }

        _pending.Clear();
        _gapWindows = 0;
        Commit(window);
    }

    private void Commit(byte[] window)
    {
        if (IsFull)
        {
            return;
        }

        var room = MaxRetainedBytes - _kept.Length;
        _kept.Write(window, 0, (int)Math.Min(room, window.Length));
        if (_kept.Length >= MaxRetainedBytes)
        {
            IsFull = true;
        }
    }
}

/// <summary>Durations in ms per RMS band: &lt;30, 30–120, 120–500, 500–2k, ≥2k.</summary>
public sealed record AskRmsBands(long Below30Ms, long Below120Ms, long Below500Ms, long Below2000Ms, long AtLeast2000Ms)
{
    public override string ToString() =>
        $"<30 {Below30Ms} ms, 30-120 {Below120Ms} ms, 120-500 {Below500Ms} ms, 500-2k {Below2000Ms} ms, >=2k {AtLeast2000Ms} ms";
}

/// <summary>
/// Stats of one ask recording. <c>KeptMs</c> excludes the zero tail (<c>TailMs</c>, set after Complete);
/// <c>LastVoicedAtMs</c> is the recording offset at the end of the last voiced window.
/// </summary>
public sealed record AskRecorderStats(
    long RecordedMs,
    long KeptMs,
    long VoicedMs,
    long? LastVoicedAtMs,
    AskRmsBands Bands,
    long TailMs,
    bool Full);
