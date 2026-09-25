using System.Globalization;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Presenting.Asking;

namespace PresenterAi.Cli;

/// <summary>
/// Plan 011 T1 support: builds a part-1 WAV of a given kept length for the cap runs. It takes the tail of the preamble
/// (the WAVs joined with 400 ms pauses), starting at a quiet 20 ms window so it opens on a word boundary, followed by
/// the part-1 question. The start is chosen by measuring: each candidate recording, part 1 + the probe's gap noise +
/// part 2, goes through <see cref="AskRecorder"/>, and the one whose kept length is nearest the target wins.
/// </summary>
internal static class ComposeAskWavCommand
{
    private const int PauseMs = 400;

    public static async Task<int> RunAsync(ComposeAskWavArguments arguments, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var question = await AskProbeCommand.LoadWavAsync(arguments.Question, "--question", error, cancellationToken).ConfigureAwait(false);
        byte[]? part2 = null;
        if (question is null || (arguments.Part2 is not null && (part2 = await AskProbeCommand.LoadWavAsync(arguments.Part2, "--part2", error, cancellationToken).ConfigureAwait(false)) is null))
        {
            return 2;
        }

        var preamble = new List<byte>();
        foreach (var path in arguments.Preambles)
        {
            var pcm = await AskProbeCommand.LoadWavAsync(path, "--preamble", error, cancellationToken).ConfigureAwait(false);
            if (pcm is null)
            {
                return 2;
            }

            preamble.AddRange(pcm);
            preamble.AddRange(new byte[PauseMs * AskRecorder.BytesPerMs]);
        }

        var result = Compose(preamble.ToArray(), question, part2 ?? [], arguments.GapSeconds, (long)Math.Round(arguments.KeptSeconds * 1000));
        if (result.KeptMs < arguments.KeptSeconds * 1000 - 1_500)
        {
            await error.WriteLineAsync(
                $"compose-ask-wav: the preamble is too short; the whole of it keeps only {Seconds(result.KeptMs)} (target {Seconds(arguments.KeptSeconds * 1000)}). Add more preamble.").ConfigureAwait(false);
            return 1;
        }

        await File.WriteAllBytesAsync(arguments.Out, PcmWav.Write24kMono(result.Part1), cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(
            $"compose-ask-wav: wrote {arguments.Out} ({Seconds(result.Part1.Length / (double)AskRecorder.BytesPerMs)} of audio); " +
            $"with {(part2 is null ? "no part 2" : "part 2")} and a {arguments.GapSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s gap the recorder keeps {Seconds(result.KeptMs)} (target {Seconds(arguments.KeptSeconds * 1000)})").ConfigureAwait(false);
        return 0;
    }

    internal sealed record Composition(byte[] Part1, long KeptMs);

    /// <summary>Picks the preamble start (a quiet window) whose recording keeps the length nearest <paramref name="targetKeptMs"/>.</summary>
    internal static Composition Compose(byte[] preamble, byte[] question, byte[] part2, double gapSeconds, long targetKeptMs)
    {
        var window = AskRecorder.WindowBytes;
        var starts = new List<int> { preamble.Length };
        for (var offset = preamble.Length - 2 * window; offset >= 0; offset -= window)
        {
            // A quiet window right before a voiced one: the tail opens in silence, on a word onset.
            if (!AudioLevel.IsVoiced(preamble.AsSpan(offset, window)) && AudioLevel.IsVoiced(preamble.AsSpan(offset + window, window)))
            {
                starts.Add(offset);
            }
        }

        starts.Add(0);
        var candidates = starts.Distinct().OrderByDescending(start => start).ToList(); // shortest tail first
        Composition? best = null;
        foreach (var start in candidates)
        {
            var part1 = Concat(preamble.AsSpan(start).ToArray(), question);
            var kept = KeptMs(part1, part2, gapSeconds);
            if (best is null || Math.Abs(kept - targetKeptMs) < Math.Abs(best.KeptMs - targetKeptMs))
            {
                best = new Composition(part1, kept);
            }

            if (kept >= targetKeptMs)
            {
                break; // longer tails only keep more
            }
        }

        return best!;
    }

    internal static long KeptMs(byte[] part1, byte[] part2, double gapSeconds)
    {
        var recorder = new AskRecorder();
        var recording = Concat(Concat(part1, AskProbeCommand.Noise(gapSeconds)), part2);
        for (var offset = 0; offset < recording.Length && !recorder.IsFull; offset += AskRecorder.WindowBytes)
        {
            recorder.Append(recording.AsSpan(offset, Math.Min(AskRecorder.WindowBytes, recording.Length - offset)));
        }

        recorder.Complete();
        return recorder.Stats.KeptMs;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }

    private static string Seconds(double ms) => (ms / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s";
}

internal sealed record ComposeAskWavArguments(IReadOnlyList<string> Preambles, string Question, string? Part2, double KeptSeconds, double GapSeconds, string Out) : CliArguments;
