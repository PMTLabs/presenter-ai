using System.Buffers.Binary;
using PresenterAi.Application.Presenting.Asking;
using Xunit;

namespace PresenterAi.Application.Tests.Presenting.Asking;

public sealed class AskRecorderTests
{
    private const int Window = AskRecorder.WindowBytes;
    private const int Voice = 1_000;

    [Fact]
    public void Silence_run_over_500_ms_becomes_320_ms_with_160_ms_kept_each_side()
    {
        var recorder = new AskRecorder();
        Feed(recorder, Windows(10, Voice));
        Feed(recorder, LabelledQuiet(60));
        Feed(recorder, Windows(10, Voice));

        var stream = Concat(recorder.Complete());
        var kept = WindowLevels(stream, windows: 36);

        Assert.Equal(Enumerable.Repeat(Voice, 10), kept[..10]);
        Assert.Equal(Enumerable.Range(1, 8).Concat(Enumerable.Range(53, 8)), kept[10..26]);
        Assert.Equal(Enumerable.Repeat(Voice, 10), kept[26..36]);
        Assert.Equal(36 * 20, recorder.Stats.KeptMs);
        Assert.Equal(80 * 20, recorder.Stats.RecordedMs);
    }

    [Fact]
    public void Pauses_up_to_500_ms_are_kept_verbatim()
    {
        var verbatim = new AskRecorder();
        Feed(verbatim, Windows(5, Voice));
        Feed(verbatim, LabelledQuiet(25));
        Feed(verbatim, Windows(5, Voice));
        var kept = WindowLevels(Concat(verbatim.Complete()), windows: 35);
        Assert.Equal(Enumerable.Range(1, 25), kept[5..30]);
        Assert.Equal(35 * 20, verbatim.Stats.KeptMs);

        // One window more and the pause is a thinking pause: 520 ms becomes 320 ms.
        var compressed = new AskRecorder();
        Feed(compressed, Windows(5, Voice));
        Feed(compressed, LabelledQuiet(26));
        Feed(compressed, Windows(5, Voice));
        compressed.Complete();
        Assert.Equal((5 + 16 + 5) * 20, compressed.Stats.KeptMs);
    }

    [Fact]
    public void Leading_and_trailing_silence_trimmed_to_160_ms_then_1000_ms_zero_tail()
    {
        var recorder = new AskRecorder();
        Feed(recorder, LabelledQuiet(50));
        Feed(recorder, Windows(10, Voice));
        Feed(recorder, LabelledQuiet(40));

        var stream = Concat(recorder.Complete());

        Assert.Equal((8 + 10 + 8) * Window + 1_000 * AskRecorder.BytesPerMs, stream.Length);
        var kept = WindowLevels(stream, windows: 26);
        Assert.Equal(Enumerable.Range(43, 8), kept[..8]);
        Assert.Equal(Enumerable.Repeat(Voice, 10), kept[8..18]);
        Assert.Equal(Enumerable.Range(1, 8), kept[18..26]);
        Assert.All(stream[(26 * Window)..], value => Assert.Equal(0, value));
        Assert.Equal(26 * 20, recorder.Stats.KeptMs);
        Assert.Equal(1_000, recorder.Stats.TailMs);
    }

    [Fact]
    public void Window_at_rms_120_is_voiced_and_119_is_quiet()
    {
        var quiet = new AskRecorder();
        Feed(quiet, Windows(1, 119));
        Assert.Equal(0, quiet.Stats.VoicedMs);
        Assert.Equal(20, quiet.Stats.Bands.Below120Ms);

        var voiced = new AskRecorder();
        Feed(voiced, Windows(1, 120));
        Assert.Equal(20, voiced.Stats.VoicedMs);
        Assert.Equal(20, voiced.Stats.Bands.Below500Ms);
    }

    [Fact]
    public void Frames_not_aligned_to_20_ms_carry_over_bytes()
    {
        var input = new byte[30 * Window + 7];
        for (var i = 0; i < input.Length / 2; i++)
        {
            var value = (short)((i % 2 == 0 ? 1 : -1) * (1_000 + i % 500));
            BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(i * 2), value);
        }

        var recorder = new AskRecorder();
        var sizes = new[] { 333, 1, 7, 959, 961, 1, 2_000, 5 };
        var offset = 0;
        for (var index = 0; offset < input.Length; index++)
        {
            var size = Math.Min(sizes[index % sizes.Length], input.Length - offset);
            recorder.Append(input.AsSpan(offset, size));
            offset += size;
        }

        Assert.Equal(30 * 20, recorder.Stats.RecordedMs);
        var stream = Concat(recorder.Complete());
        Assert.Equal(input[..(30 * Window)], stream[..(30 * Window)]);
        Assert.Equal(30 * Window + 1_000 * AskRecorder.BytesPerMs, stream.Length);
    }

    [Fact]
    public void Under_200_ms_voiced_is_not_speech()
    {
        var recorder = new AskRecorder();
        Feed(recorder, Windows(9, Voice));
        Feed(recorder, LabelledQuiet(40));
        Assert.False(recorder.HasSpeech);
        Assert.Equal(180, recorder.Stats.VoicedMs);

        Feed(recorder, Windows(1, Voice));
        Assert.True(recorder.HasSpeech);
    }

    [Fact]
    public void Reports_full_at_120_seconds_retained_and_ignores_silence_toward_the_cap()
    {
        var recorder = new AskRecorder();
        Assert.False(Feed(recorder, Windows(5_000, Voice)));
        Assert.False(Feed(recorder, Windows(3_000, 0)));
        Assert.False(Feed(recorder, Windows(950, Voice)));
        Assert.False(recorder.IsFull);
        Assert.Equal(119_320, recorder.Stats.KeptMs);
        Assert.Equal(179_000, recorder.Stats.RecordedMs);

        Assert.True(Feed(recorder, Windows(50, Voice)));
        Assert.True(recorder.IsFull);
        Assert.True(recorder.Stats.Full);
        Assert.Equal(AskRecorder.MaxRetainedMs, recorder.Stats.KeptMs);

        var recorded = recorder.Stats.RecordedMs;
        Assert.True(Feed(recorder, Windows(10, Voice)));
        Assert.Equal(recorded, recorder.Stats.RecordedMs);
        Assert.Equal(AskRecorder.MaxRetainedMs, recorder.Stats.KeptMs);
    }

    [Fact]
    public void Chunks_are_200_ms_and_concatenate_to_the_compressed_stream()
    {
        var recorder = new AskRecorder();
        var voice = Windows(37, Voice);
        Feed(recorder, voice);

        var chunks = recorder.Complete();

        Assert.Equal(9, chunks.Count);
        Assert.All(chunks.Take(8), chunk => Assert.Equal(AskRecorder.ChunkBytes, chunk.Length));
        Assert.Equal((37 * 20 + 1_000 - 8 * 200) * AskRecorder.BytesPerMs, chunks[^1].Length);
        var expected = voice.SelectMany(window => window).Concat(new byte[1_000 * AskRecorder.BytesPerMs]).ToArray();
        Assert.Equal(expected, Concat(chunks));
    }

    [Fact]
    public void Stats_bands_and_last_voice_time_are_reported()
    {
        var recorder = new AskRecorder();
        Feed(recorder, Windows(2, 10));
        Feed(recorder, Windows(3, 50));
        Feed(recorder, Windows(4, 200));
        Feed(recorder, Windows(5, 1_000));
        Feed(recorder, Windows(6, 3_000));
        Feed(recorder, Windows(7, 10));

        var stats = recorder.Stats;

        Assert.Equal(new AskRmsBands(180, 60, 80, 100, 120), stats.Bands);
        Assert.Equal(400, stats.LastVoicedAtMs);
        Assert.Equal(300, stats.VoicedMs);
        Assert.Equal(540, stats.RecordedMs);
        Assert.Equal(0, stats.TailMs);
    }

    [Fact]
    public void Raw_mode_keeps_every_window_and_still_adds_the_tail()
    {
        var recorder = new AskRecorder(compress: false);
        Feed(recorder, LabelledQuiet(50));
        Feed(recorder, Windows(10, Voice));
        Feed(recorder, LabelledQuiet(60));

        var stream = Concat(recorder.Complete());

        Assert.Equal(120 * Window + 1_000 * AskRecorder.BytesPerMs, stream.Length);
        Assert.Equal(120 * 20, recorder.Stats.KeptMs);
    }

    [Theory]
    [InlineData(499, 320)]
    [InlineData(2_001, 320)]
    [InlineData(1_000, 199)]
    [InlineData(1_000, 401)]
    public void Retuning_is_bounded(int tailMs, int gapKeepMs)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AskRecorder(tailMs, gapKeepMs));
    }

    [Fact]
    public void Retuned_gap_and_tail_are_applied()
    {
        var recorder = new AskRecorder(tailSilenceMs: 500, gapKeepMs: 200);
        Feed(recorder, Windows(5, Voice));
        Feed(recorder, LabelledQuiet(60));
        Feed(recorder, Windows(5, Voice));

        var stream = Concat(recorder.Complete());

        var kept = WindowLevels(stream, windows: 20);
        Assert.Equal(Enumerable.Range(1, 5).Concat(Enumerable.Range(56, 5)), kept[5..15]);
        Assert.Equal(20 * Window + 500 * AskRecorder.BytesPerMs, stream.Length);
    }

    private static bool Feed(AskRecorder recorder, IEnumerable<byte[]> windows)
    {
        var full = false;
        foreach (var window in windows)
        {
            full = recorder.Append(window);
        }

        return full;
    }

    /// <summary>Windows whose samples alternate ±level, so the RMS is exactly <paramref name="level"/>.</summary>
    private static List<byte[]> Windows(int count, int level) =>
        Enumerable.Range(0, count).Select(_ => Level(level)).ToList();

    /// <summary>Quiet windows labelled 1..count by their (sub-threshold) level, so kept windows can be identified.</summary>
    private static List<byte[]> LabelledQuiet(int count)
    {
        Assert.True(count < AskRecorder.VoiceRms);
        return Enumerable.Range(1, count).Select(Level).ToList();
    }

    private static byte[] Level(int level)
    {
        var window = new byte[Window];
        for (var i = 0; i < Window / 2; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(window.AsSpan(i * 2), (short)(i % 2 == 0 ? level : -level));
        }

        return window;
    }

    private static int[] WindowLevels(byte[] stream, int windows) =>
        Enumerable.Range(0, windows)
            .Select(index => (int)BinaryPrimitives.ReadInt16LittleEndian(stream.AsSpan(index * Window, 2)))
            .ToArray();

    private static byte[] Concat(IReadOnlyList<ReadOnlyMemory<byte>> chunks) =>
        chunks.SelectMany(chunk => chunk.ToArray()).ToArray();
}
