using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Presenting.Asking;
using PresenterAi.Infrastructure.Live;

namespace PresenterAi.Cli;

/// <summary>
/// Plan 011 T1: the press-to-ask live probe. It narrates a one-slide probe deck, checks that a muted upstream stays
/// silent, then sends a silence-compressed two-part question in one unpaced burst and observes whether GPT-Live answers
/// it once, as one question. It prints transcripts, timings and the input-clock provenance, never audio or secrets.
/// </summary>
internal static class AskProbeCommand
{
    private const int BytesPerMs = AskRecorder.BytesPerMs;
    private const int FrameBytes = AskRecorder.WindowBytes;
    private const int QuietAfterNarrationMs = 3_000;
    private const int MuteControlSpeechMs = 3_000;
    private const int MuteControlSettleMs = 2_000;
    private const int ContinueAfterMs = 3_000;
    private const int AnswerStartWaitMs = 60_000;
    private const int AnswerEndQuietMs = 3_000;
    private const int AnswerMaxMs = 90_000;
    private const int SecondResponseWindowMs = 15_000;
    private const int OutputClockJumpMs = 1_000;
    private const string SegmentRule =
        "a new response starts only after >= 3 s without assistant output AND an output-clock jump (next assistant delta start_ms > previous end_ms + 1000 ms, or no timestamps); voiced-audio gaps alone never split";
    private const int ReplyObserveMs = 10_000;
    private const int ProvenanceSlackMs = 250;
    private const int VoicedIntervalGapMs = 500;
    private const int EndSignalSettleMs = 500;
    private const string WavHint = "Convert it with: ffmpeg -i <in> -ar 24000 -ac 1 -c:a pcm_s16le <out.wav>";

    public static async Task<int> RunAsync(
        AskProbeArguments arguments,
        IConfiguration configuration,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var part1 = await LoadWavAsync(arguments.Part1, "--part1", error, cancellationToken).ConfigureAwait(false);
        var part2 = await LoadWavAsync(arguments.Part2, "--part2", error, cancellationToken).ConfigureAwait(false);
        var reply = arguments.Reply is null ? null : await LoadWavAsync(arguments.Reply, "--reply", error, cancellationToken).ConfigureAwait(false);
        var absent = arguments.Absent is null ? null : await LoadWavAsync(arguments.Absent, "--absent", error, cancellationToken).ConfigureAwait(false);
        if (part1 is null || part2 is null || (arguments.Reply is not null && reply is null) || (arguments.Absent is not null && absent is null))
        {
            return 2;
        }

        await using var services = Program.BuildServices(configuration, Directory.GetCurrentDirectory());
        _ = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<UpstreamOptions>>().Value;
        var routes = services.GetRequiredService<UpstreamRoutes>();
        var route = SmokeCommand.SelectRoute(routes, arguments.Provider);
        if (route is null)
        {
            await error.WriteLineAsync($"Provider not configured: {arguments.Provider}").ConfigureAwait(false);
            return 2;
        }

        var deck = ProbeDeck.For(arguments.Lang);
        var factory = services.GetRequiredService<ILiveSessionFactory>();
        var managed = !string.IsNullOrWhiteSpace(route.DelegationModel);
        var slides = new[] { new SlideRef(0, deck.SlideTitle) };
        var config = new LiveSessionConfig(
            route.Model,
            PromptBuilder.SystemInstructions(deck.Title, slides, managedMode: managed),
            routes.Voice,
            deck.Title,
            DelegationInstructions: managed ? PromptBuilder.BackendInstructions(deck.Title, slides) : null);

        await output.WriteLineAsync(
            $"ask-probe: provider={arguments.Provider} route={route.Name} model={route.Model} delegation={(managed ? route.DelegationModel : "client")} " +
            $"lang={arguments.Lang} variant={arguments.Variant} gap={Seconds(arguments.GapSeconds * 1000)} tail-ms={arguments.TailMs} gap-keep-ms={arguments.GapKeepMs} " +
            $"at {DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}").ConfigureAwait(false);

        try
        {
            return arguments.ObserveInterrupt
                ? await ObserveInterruptAsync(factory, route, config, deck, absent ?? part1, absent is null, output, cancellationToken).ConfigureAwait(false)
                : await ProbeAsync(factory, route, config, deck, arguments, part1, part2, reply, output, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync($"ask-probe failed: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<int> ProbeAsync(
        ILiveSessionFactory factory,
        UpstreamRoute route,
        LiveSessionConfig config,
        ProbeDeck deck,
        AskProbeArguments arguments,
        byte[] part1,
        byte[] part2,
        byte[]? reply,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var session = factory.Create(route, config);
        var observer = new Observer(session);
        double? usage = null;
        try
        {
            // 1. Connect like the presenter and narrate the probe slide.
            await ConnectAndNarrateAsync(session, observer, deck, output, cancellationToken).ConfigureAwait(false);
            var endSignalTypes = observer.EndSignalTypes();
            await output.WriteLineAsync(endSignalTypes.Count > 0
                ? $"end-of-response events seen during narration: {string.Join(", ", endSignalTypes)}; the answer ends on one"
                : "end-of-response events seen during narration: none; the answer ends after 3 s without assistant audio or transcript").ConfigureAwait(false);

            // 2. Mute control: a muted upstream must neither answer nor transcribe.
            var muteAt = observer.Now;
            if (!session.Mute() || session.AppendInstructions(PromptBuilder.PauseInstruction(), "pause-1") is null)
            {
                throw new InvalidOperationException("could not mute the upstream");
            }

            await SendPacedAsync(session, part1.AsMemory(0, Math.Min(part1.Length, MuteControlSpeechMs * BytesPerMs)), cancellationToken).ConfigureAwait(false);
            await Task.Delay(MuteControlSettleMs, cancellationToken).ConfigureAwait(false);
            var muteControlEnd = observer.Now;

            // 3. The recording: part 1, the gap as low noise (RMS about 20), part 2, through the recorder in 20 ms frames.
            var recorder = new AskRecorder(arguments.TailMs, arguments.GapKeepMs, compress: arguments.Variant != "raw");
            var recording = Concat(part1, Noise(arguments.GapSeconds), part2);
            for (var offset = 0; offset < recording.Length; offset += FrameBytes)
            {
                recorder.Append(recording.AsSpan(offset, Math.Min(FrameBytes, recording.Length - offset)));
            }

            if (!recorder.HasSpeech)
            {
                throw new InvalidOperationException($"the recording holds under {AskRecorder.MinSpeechMs} ms of voiced audio");
            }

            var chunks = recorder.Complete();
            var stats = recorder.Stats;
            await output.WriteLineAsync(
                $"recorder: recorded {Seconds(stats.RecordedMs)}, kept {Seconds(stats.KeptMs)} (voiced {Seconds(stats.VoicedMs)}) + {Seconds(stats.TailMs)} tail, " +
                $"{chunks.Count} chunks, last voice at {Seconds(stats.LastVoicedAtMs ?? 0)}; rms bands {stats.Bands}{(stats.Full ? "; CAP REACHED (120 s): part 2 may be cut" : string.Empty)}").ConfigureAwait(false);

            // 4. Ask done: unmute, mark, burst, mark.
            observer.AnswerClientDelegations = true;
            var askDoneAt = observer.Now;
            if (!session.Unmute())
            {
                throw new InvalidOperationException("unmute refused");
            }

            var startMark = session.MarkInputPosition();
            var refused = 0;
            foreach (var chunk in chunks)
            {
                refused += session.SendAudio(chunk) ? 0 : 1;
            }

            var lastQueuedAt = observer.Now;
            var endMark = session.MarkInputPosition();
            if (startMark is null || endMark is null || refused > 0)
            {
                throw new InvalidOperationException($"burst not fully queued ({refused} of {chunks.Count} appends refused)");
            }

            var marks = await observer.WaitForMarksAsync([startMark, endMark], 30_000, cancellationToken).ConfigureAwait(false);
            var burstStartMs = marks[startMark].SentMs;
            var burstEndMs = marks[endMark].SentMs;
            var sendDurationMs = marks[endMark].At - askDoneAt;
            await output.WriteLineAsync(
                $"burst: {chunks.Count} chunks queued in {lastQueuedAt - askDoneAt} ms, on the wire after {sendDurationMs} ms; " +
                $"input clock [{burstStartMs}, {burstEndMs}] ms").ConfigureAwait(false);

            long? continueAt = null;
            if (arguments.Variant == "continue")
            {
                while (observer.Now < lastQueuedAt + ContinueAfterMs && observer.FirstVoicedAfter(askDoneAt) is null)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }

                if (observer.FirstVoicedAfter(askDoneAt) is null)
                {
                    continueAt = observer.Now;
                    session.ContinueResponses();
                    await output.WriteLineAsync($"continue: response.create sent at +{continueAt - askDoneAt} ms after Ask done").ConfigureAwait(false);
                }
            }

            // 5. Observe the answer, its end and the 15 s after it.
            var answerStart = await observer.WaitForVoicedAfterAsync(lastQueuedAt, AnswerStartWaitMs, cancellationToken).ConfigureAwait(false);
            long? answerEnd = null;
            if (answerStart is not null)
            {
                answerEnd = await observer.WaitForAnswerEndAsync(answerStart.Value, AnswerEndQuietMs, answerStart.Value + AnswerMaxMs, endSignalTypes, cancellationToken).ConfigureAwait(false);
                // A delivery stall can continue the answer after the quiet: keep the 15 s window behind the answer's end
                // as the output-clock rule computes it from what has arrived so far.
                var capAt = answerStart.Value + AnswerMaxMs + SecondResponseWindowMs;
                while (answerEnd is not null && observer.Now < capAt)
                {
                    var segments = Segments(askDoneAt, endSignalTypes.Count > 0, endSignalTypes, observer.Snapshot());
                    var answerEndSoFar = segments.FirstOrDefault(segment => segment.End >= answerStart.Value)?.End ?? answerEnd.Value;
                    if (observer.Now >= Math.Max(answerEnd.Value, answerEndSoFar) + SecondResponseWindowMs)
                    {
                        break;
                    }

                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }

            // 4a. The live reply ("yes") at real-time pace.
            long? replyAt = null;
            long? replyMarkMs = null;
            if (reply is not null)
            {
                replyAt = observer.Now;
                var replyMark = session.MarkInputPosition();
                await SendPacedAsync(session, reply, cancellationToken).ConfigureAwait(false);
                if (replyMark is not null)
                {
                    var replyMarks = await observer.WaitForMarksAsync([replyMark], 5_000, cancellationToken).ConfigureAwait(false);
                    replyMarkMs = replyMarks[replyMark].SentMs;
                }

                await Task.Delay(ReplyObserveMs, cancellationToken).ConfigureAwait(false);
            }

            var close = await session.CloseAsync().ConfigureAwait(false);
            usage = close.Seconds ?? observer.LastUsage;

            var run = new ProbeRun(deck, muteAt, muteControlEnd, askDoneAt, lastQueuedAt, marks[endMark].At, burstStartMs, burstEndMs,
                continueAt, answerStart, answerEnd, replyAt, replyMarkMs, sendDurationMs, stats, arguments.Trace, endSignalTypes.Count > 0, endSignalTypes);
            var passed = await ReportAsync(run, observer.Snapshot(), output).ConfigureAwait(false);
            await output.WriteLineAsync($"closed: reason={close.Reason}").ConfigureAwait(false);
            await output.WriteLineAsync($"usage.seconds={FormatUsage(usage)}").ConfigureAwait(false);
            return passed ? 0 : 1;
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static async Task<bool> ReportAsync(ProbeRun run, IReadOnlyList<ProbeEvent> events, TextWriter output)
    {
        var deck = run.Deck;
        if (run.Trace)
        {
            await WriteTraceAsync(run, events, output).ConfigureAwait(false);
        }

        var errors = events.Where(e => e.Kind == EventKind.Error).ToList();
        foreach (var e in errors)
        {
            await output.WriteLineAsync($"upstream error +{e.At} ms: {e.Text}").ConfigureAwait(false);
        }

        // (ii) mute control, (iii) nothing voiced before the last chunk was queued.
        var muteVoicedMs = VoicedMs(events, run.MuteAt, run.MuteControlEnd);
        var muteUserDeltas = events.Count(e => e.Kind == EventKind.User && e.At >= run.MuteAt && e.At < run.MuteControlEnd && e.Text.Trim().Length > 0);
        var earlyVoicedMs = VoicedMs(events, run.MuteAt, run.LastQueuedAt);

        // (iv) User turns after the burst. Rule: a user delta whose start_ms lies in the burst range on the input clock
        // ([start mark, end mark + 250 ms), provenance P-13) is burst text and belongs to the one burst turn whatever
        // arrived in between. Any other user delta before the reply is grouped by arrival order, and assistant output
        // (voiced audio, assistant transcript, delegation, tool call) arriving between two such deltas starts a new turn.
        var observeEnd = run.ReplyAt ?? long.MaxValue;
        var userAfter = events.Where(e => e.Kind == EventKind.User && e.At >= run.AskDoneAt && e.At < observeEnd && e.Text.Trim().Length > 0).ToList();
        var burstDeltas = userAfter.Where(e => InBurst(run, e)).OrderBy(e => e.StartMs).ThenBy(e => e.At).ToList();
        var otherTurns = new List<List<ProbeEvent>>();
        List<ProbeEvent>? current = null;
        foreach (var e in events.Where(e => e.At >= run.AskDoneAt && e.At < observeEnd))
        {
            if (e.Kind == EventKind.User && e.Text.Trim().Length > 0 && !InBurst(run, e))
            {
                if (current is null)
                {
                    current = [];
                    otherTurns.Add(current);
                }

                current.Add(e);
            }
            else if (IsAssistantOutput(e))
            {
                current = null;
            }
        }

        var turnCount = (burstDeltas.Count > 0 ? 1 : 0) + otherTurns.Count;
        var burstText = string.Concat(burstDeltas.Select(e => e.Text));
        var part1At = Find(burstText, deck.Part1Keywords);
        var part2At = Find(burstText, deck.Part2Keywords);
        var between = 0;
        if (part1At >= 0 && part2At > part1At)
        {
            // Assistant output that arrived between the delta completing the part-1 keyword and the delta starting the
            // part-2 keyword means the upstream responded between the two halves.
            var part1Delta = DeltaAt(burstDeltas, part1At + Canonical(deck.Part1Keywords.First(k => Find(burstText, [k]) == part1At)).Length - 1);
            var part2Delta = DeltaAt(burstDeltas, part2At);
            var from = Math.Min(part1Delta.At, part2Delta.At);
            var to = Math.Max(part1Delta.At, part2Delta.At);
            between = events.Count(e => IsAssistantOutput(e) && e.At > from && e.At < to);
        }

        // Assistant speech segments after the burst (arrival order of voiced audio and assistant transcript): a new
        // segment starts after 3 s without either, or after an upstream end-of-response signal when one is in use.
        var segments = Segments(run.AskDoneAt, run.SignalEnd, run.EndSignalTypes, events);
        var answer = run.AnswerStart is { } answerStart ? segments.FirstOrDefault(s => s.End >= answerStart) : null;
        // The answer ends at its segment's last output (the output-clock rule); null when nothing ended within the cap.
        var answerEnd = run.AnswerEnd is null ? null : answer?.End ?? run.AnswerEnd;
        var nextSegment = answer is null ? null : segments.SkipWhile(segment => !ReferenceEquals(segment, answer)).Skip(1).FirstOrDefault();
        var secondResponseAt = answerEnd is { } end && nextSegment is not null && nextSegment.Start <= end + SecondResponseWindowMs
            ? nextSegment.Start
            : (long?)null;
        var answerText = answer is null
            ? string.Empty
            : string.Concat(events.Where(e => e.Kind == EventKind.Assistant && e.At >= answer.Start && e.At <= (answerEnd ?? answer.End)).Select(e => e.Text));
        var factA = Quote(answerText, deck.FactA);
        var factB = Quote(answerText, deck.FactB);

        await output.WriteLineAsync("user turns after the burst:").ConfigureAwait(false);
        if (burstDeltas.Count > 0)
        {
            await output.WriteLineAsync($"  burst turn: \"{burstText.Trim()}\"").ConfigureAwait(false);
        }

        for (var index = 0; index < otherTurns.Count; index++)
        {
            await output.WriteLineAsync($"  other turn {index + 1}: \"{string.Concat(otherTurns[index].Select(e => e.Text)).Trim()}\"").ConfigureAwait(false);
        }

        await output.WriteLineAsync($"assistant speech segments after the burst (rule: {SegmentRule}{(run.SignalEnd ? $"; also split on {string.Join('/', run.EndSignalTypes)}" : "; no end-of-response event seen during narration")}):").ConfigureAwait(false);
        foreach (var segment in segments)
        {
            await output.WriteLineAsync(
                $"  +{segment.Start - run.AskDoneAt}..+{segment.End - run.AskDoneAt} ms after Ask done, {Seconds(VoicedMs(events, segment.Start, segment.End + 1))} voiced" +
                $"{(ReferenceEquals(segment, answer) ? " [answer]" : string.Empty)}: \"{string.Concat(events.Where(e => e.Kind == EventKind.Assistant && e.At >= segment.Start && e.At <= segment.End).Select(e => e.Text)).Trim()}\"").ConfigureAwait(false);
        }

        await output.WriteLineAsync($"answer: \"{answerText.Trim()}\" (ended by {(answerEnd is null ? "nothing within the cap" : run.SignalEnd ? "an end-of-response event" : "its last output that continues on the output clock, plus 3 s quiet")})").ConfigureAwait(false);
        foreach (var e in events.Where(e => e.Kind is EventKind.Delegation or EventKind.DelegationDone or EventKind.Tool && e.At >= run.MuteAt))
        {
            await output.WriteLineAsync($"  {e.Kind.ToString().ToLowerInvariant()} +{e.At - run.AskDoneAt} ms after Ask done: {e.Text}").ConfigureAwait(false);
        }

        // Latencies (the answer-start budget comes from Ask done → first answer audio).
        if (run.AnswerStart is { } start)
        {
            await output.WriteLineAsync(
                $"latency: Ask done -> first answer audio {start - run.AskDoneAt} ms; last chunk queued -> {start - run.LastQueuedAt} ms; " +
                $"end mark on the wire -> {start - run.EndMarkAt} ms; answer {(answerEnd is { } e2 ? $"ended +{e2 - run.AskDoneAt} ms, {Seconds(VoicedMs(events, start, e2 + 1))} voiced" : "did not end within the cap")}").ConfigureAwait(false);
            var lastBurstDelta = burstDeltas.MaxBy(e => e.At);
            if (lastBurstDelta is not null)
            {
                await output.WriteLineAsync(
                    $"late transcript (R5): last burst delta arrived {lastBurstDelta.At - start} ms after answer start" +
                    (answerEnd is { } e3 ? $", {lastBurstDelta.At - e3} ms after answer end" : string.Empty)).ConfigureAwait(false);
            }
        }
        else
        {
            await output.WriteLineAsync($"latency: no answer audio within {AnswerStartWaitMs / 1000} s after the last chunk").ConfigureAwait(false);
        }

        // Provenance (P-13): every user delta before the reply starts before the burst end on the input clock; the reply
        // starts after it.
        await output.WriteLineAsync($"provenance: burst range [{run.BurstStartMs}, {run.BurstEndMs}] ms on the input clock (kept audio starts about {AskRecorder.LeadMs} ms after the start mark)").ConfigureAwait(false);
        var burstOk = userAfter.Count > 0;
        foreach (var e in userAfter)
        {
            var ok = e.StartMs is { } s && s < run.BurstEndMs + ProvenanceSlackMs;
            burstOk &= ok;
            await output.WriteLineAsync($"  burst delta +{e.At - run.AskDoneAt} ms start_ms={Ms(e.StartMs)} end_ms={Ms(e.EndMs)} {(ok ? "ok" : "OUT OF RANGE")} \"{e.Text}\"").ConfigureAwait(false);
        }

        var replyOk = true;
        var replyDeltas = run.ReplyAt is { } replyAt
            ? events.Where(e => e.Kind == EventKind.User && e.At >= replyAt && e.Text.Trim().Length > 0).ToList()
            : [];
        if (run.ReplyAt is null)
        {
            await output.WriteLineAsync("  reply: not run (no --reply)").ConfigureAwait(false);
        }
        else
        {
            replyOk = replyDeltas.Count > 0;
            await output.WriteLineAsync($"  reply sent from input clock {Ms(run.ReplyMarkMs)} ms").ConfigureAwait(false);
            foreach (var e in replyDeltas)
            {
                var ok = e.StartMs is { } s && s >= run.BurstEndMs;
                replyOk &= ok;
                await output.WriteLineAsync($"  reply delta +{e.At - run.ReplyAt!.Value} ms start_ms={Ms(e.StartMs)} end_ms={Ms(e.EndMs)} {(ok ? "ok" : "BEFORE BURST END")} \"{e.Text}\"").ConfigureAwait(false);
            }

            if (replyDeltas.Count == 0)
            {
                await output.WriteLineAsync("  reply: no user transcript delta arrived").ConfigureAwait(false);
            }
        }

        var turnReasons = new List<string>();
        if (turnCount != 1)
        {
            turnReasons.Add($"{turnCount} user turns (burst turn {(burstDeltas.Count > 0 ? "present" : "absent")}, {otherTurns.Count} other)");
        }

        if (part1At < 0)
        {
            turnReasons.Add("part-1 keyword missing");
        }

        if (part2At < 0)
        {
            turnReasons.Add("part-2 keyword missing");
        }

        if (part1At >= 0 && part2At >= 0 && part2At < part1At)
        {
            turnReasons.Add("part-2 keyword before part-1 keyword");
        }

        if (between > 0)
        {
            turnReasons.Add($"{between} assistant/delegation events arrived between the part-1 and part-2 keywords");
        }

        var criteria = new (string Name, bool Pass, string Detail)[]
        {
            ("(i) no upstream error", errors.Count == 0, $"{errors.Count} errors"),
            ("(ii) mute control silent", muteVoicedMs == 0 && muteUserDeltas == 0, $"{muteVoicedMs} ms voiced assistant audio, {muteUserDeltas} user deltas while muted"),
            ("(iii) nothing voiced before the last chunk was queued", earlyVoicedMs == 0, $"{earlyVoicedMs} ms voiced"),
            ("(iv) exactly one user turn, part 1 before part 2", turnReasons.Count == 0,
                turnReasons.Count == 0 ? "one turn; part-1 keyword before part-2 keyword; nothing between them" : string.Join("; ", turnReasons)),
            ("(v) one answer with fact A and fact B", answer is not null && factA is not null && factB is not null,
                $"fact A {(factA is null ? "missing" : $"\"{factA}\"")}; fact B {(factB is null ? "missing" : $"\"{factB}\"")}"),
            ("(vi) no second unsolicited response within 15 s", answerEnd is not null && secondResponseAt is null,
                answerEnd is null ? "the answer did not end" : secondResponseAt is null ? "none" : $"a new response started {secondResponseAt - answerEnd} ms after the answer ended")
        };

        foreach (var (name, pass, detail) in criteria)
        {
            await output.WriteLineAsync($"{(pass ? "PASS" : "FAIL")} {name}: {detail}").ConfigureAwait(false);
        }

        var provenance = burstOk && replyOk;
        await output.WriteLineAsync($"provenance check: {(provenance ? "CONFIRMED" : "NOT CONFIRMED")} (burst deltas {(burstOk ? "in range" : "not all in range")}; reply {(run.ReplyAt is null ? "not run" : replyOk ? "after burst end" : "not after burst end")})").ConfigureAwait(false);
        var passed = criteria.All(criterion => criterion.Pass);
        await output.WriteLineAsync($"result: {(passed ? "PASS" : "FAIL")} (variant run; send duration {run.SendDurationMs} ms, kept {Seconds(run.Stats.KeptMs)})").ConfigureAwait(false);
        return passed;
    }

    private static bool InBurst(ProbeRun run, ProbeEvent e) =>
        e.StartMs is { } start && start >= run.BurstStartMs && start < run.BurstEndMs + ProvenanceSlackMs;

    private static bool IsAssistantOutput(ProbeEvent e) =>
        e.Kind is EventKind.Voiced or EventKind.Delegation or EventKind.Tool
        || (e.Kind == EventKind.Assistant && e.Text.Trim().Length > 0);

    /// <summary>The burst delta that holds canonical position <paramref name="position"/> of the concatenated burst text.</summary>
    private static ProbeEvent DeltaAt(IReadOnlyList<ProbeEvent> deltas, int position)
    {
        var text = string.Empty;
        foreach (var delta in deltas)
        {
            text += delta.Text;
            if (Canonical(text).Length > position)
            {
                return delta;
            }
        }

        return deltas[^1];
    }

    internal sealed record Segment(long Start, long End);

    /// <summary>
    /// Assistant speech segments after Ask done, by arrival order of voiced audio and assistant transcript. A new segment
    /// starts only when BOTH hold: at least 3 s passed without assistant output, and the next assistant transcript delta
    /// jumps on the output clock (its start_ms is more than 1 s after the previous delta's end_ms, or either has no
    /// timestamps). Voiced audio never splits: audio arriving after a quiet waits for the next transcript delta to decide,
    /// and joins the current segment when none follows. An end-of-response event, when in use, also ends a segment.
    /// </summary>
    internal static List<Segment> Segments(long askDoneAt, bool signalEnd, IReadOnlySet<string> endTypes, IReadOnlyList<ProbeEvent> events)
    {
        var segments = new List<Segment>();
        long? start = null, last = null, previousEndMs = null;
        var pending = new List<long>();
        var signalled = false;
        foreach (var e in events.Where(e => e.At >= askDoneAt))
        {
            if (signalEnd && e.Kind == EventKind.Raw && endTypes.Contains(e.Type ?? string.Empty) && start is not null)
            {
                signalled = true;
                pending.Clear();
                last = e.At;
                continue;
            }

            var isTranscript = e.Kind == EventKind.Assistant && e.Text.Length > 0;
            if (e.Kind != EventKind.Voiced && !isTranscript)
            {
                continue;
            }

            if (start is null || signalled)
            {
                if (start is not null)
                {
                    segments.Add(new Segment(start.Value, last!.Value));
                }

                start = e.At;
                last = e.At;
                previousEndMs = isTranscript ? e.EndMs : null;
                signalled = false;
                continue;
            }

            if (!isTranscript)
            {
                if (pending.Count > 0 || e.At - last >= AnswerEndQuietMs)
                {
                    pending.Add(e.At);
                }
                else
                {
                    last = e.At;
                }

                continue;
            }

            var quietFrom = last!.Value;
            var quiet = (pending.Count > 0 ? pending[0] : e.At) - quietFrom >= AnswerEndQuietMs;
            var jump = e.StartMs is null || previousEndMs is null || e.StartMs > previousEndMs + OutputClockJumpMs;
            if (quiet && jump)
            {
                segments.Add(new Segment(start.Value, quietFrom));
                start = pending.Count > 0 ? pending[0] : e.At;
            }

            last = e.At;
            previousEndMs = e.EndMs ?? previousEndMs;
            pending.Clear();
        }

        if (start is not null)
        {
            segments.Add(new Segment(start.Value, pending.Count > 0 ? pending[^1] : last!.Value));
        }

        return segments;
    }

    /// <summary>--trace: every upstream event from Ask done on, by arrival; voiced audio as start/stop intervals.</summary>
    private static async Task WriteTraceAsync(ProbeRun run, IReadOnlyList<ProbeEvent> events, TextWriter output)
    {
        var lines = new List<(long At, int Order, string Text)>();
        long? voicedStart = null, voicedLast = null;
        double voicedMs = 0;
        foreach (var e in events.Where(e => e.At >= run.AskDoneAt))
        {
            switch (e.Kind)
            {
                case EventKind.Voiced:
                    if (voicedStart is not null && e.At - voicedLast > VoicedIntervalGapMs)
                    {
                        lines.Add((voicedLast!.Value, 1, $"voiced audio stop ({Math.Round(voicedMs)} ms voiced since start)"));
                        voicedStart = null;
                    }

                    if (voicedStart is null)
                    {
                        voicedStart = e.At;
                        voicedMs = 0;
                        lines.Add((e.At, 0, "voiced audio start"));
                    }

                    voicedLast = e.At;
                    voicedMs += e.VoicedMs;
                    break;
                case EventKind.User:
                    lines.Add((e.At, 0, $"user transcript start_ms={Ms(e.StartMs)} end_ms={Ms(e.EndMs)} \"{e.Text}\"{(InBurst(run, e) ? " [burst range]" : string.Empty)}"));
                    break;
                case EventKind.Assistant:
                    lines.Add((e.At, 0, $"assistant transcript start_ms={Ms(e.StartMs)} end_ms={Ms(e.EndMs)} \"{e.Text}\""));
                    break;
                case EventKind.Raw:
                    lines.Add((e.At, 0, $"{e.Type}{(run.EndSignalTypes.Contains(e.Type ?? string.Empty) ? " [end-of-response]" : string.Empty)} {e.Text}".TrimEnd()));
                    break;
                case EventKind.Mark:
                    lines.Add((e.At, 0, $"input mark {e.Text}"));
                    break;
                default:
                    lines.Add((e.At, 0, $"{e.Kind.ToString().ToLowerInvariant()} {e.Text}"));
                    break;
            }
        }

        if (voicedStart is not null)
        {
            lines.Add((voicedLast!.Value, 1, $"voiced audio stop ({Math.Round(voicedMs)} ms voiced since start)"));
        }

        await output.WriteLineAsync($"trace (ms after Ask done; voiced intervals merge deltas under {VoicedIntervalGapMs} ms apart):").ConfigureAwait(false);
        foreach (var (at, _, text) in lines.OrderBy(line => line.At).ThenBy(line => line.Order))
        {
            await output.WriteLineAsync($"  +{at - run.AskDoneAt,6} {text}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// An upstream event that says the model's output ended: not an input event, not the delegation envelope, and its
    /// last name segment is done/completed/ended/… while the name mentions output, response, turn or audio.
    /// </summary>
    internal static bool IsEndOfResponseType(string type)
    {
        if (type.Contains("input", StringComparison.Ordinal) || type == "response.event")
        {
            return false;
        }

        var last = type[(type.LastIndexOf('.') + 1)..];
        return last is "done" or "completed" or "complete" or "end" or "ended" or "finished" or "stopped"
            && (type.Contains("output", StringComparison.Ordinal) || type.Contains("response", StringComparison.Ordinal)
                || type.Contains("turn", StringComparison.Ordinal) || type.Contains("audio", StringComparison.Ordinal));
    }

    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "key", "api_key", "api-key", "authorization", "token", "access_token", "secret", "password", "client_secret"
    };

    private static readonly HashSet<string> BulkyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio", "instructions", "tools", "session", "headers"
    };

    /// <summary>Scalar fields of an upstream event (nested up to 3 levels) for the trace: no audio, no instructions, no secrets.</summary>
    internal static string Summarize(JsonElement message)
    {
        var parts = new List<string>();
        void Walk(JsonElement element, string path, int depth)
        {
            foreach (var property in element.EnumerateObject())
            {
                var name = property.Name;
                if (depth == 0 && name == "type")
                {
                    continue;
                }

                if (SecretKeys.Contains(name) || BulkyKeys.Contains(name))
                {
                    parts.Add($"{path}{name}=<omitted>");
                    continue;
                }

                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.Object when depth < 3:
                        Walk(property.Value, $"{path}{name}.", depth + 1);
                        break;
                    case JsonValueKind.Object:
                    case JsonValueKind.Array:
                        parts.Add($"{path}{name}=<{property.Value.ValueKind.ToString().ToLowerInvariant()}>");
                        break;
                    case JsonValueKind.String:
                        var text = property.Value.GetString() ?? string.Empty;
                        text = text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
                        parts.Add($"{path}{name}=\"{(text.Length > 160 ? text[..160] + "…" : text)}\"");
                        break;
                    default:
                        parts.Add($"{path}{name}={property.Value.GetRawText()}");
                        break;
                }
            }
        }

        if (message.ValueKind == JsonValueKind.Object)
        {
            Walk(message, string.Empty, 0);
        }

        return string.Join(' ', parts);
    }

    private static async Task<int> ObserveInterruptAsync(
        ILiveSessionFactory factory,
        UpstreamRoute route,
        LiveSessionConfig config,
        ProbeDeck deck,
        byte[] question,
        bool questionIsPart1,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        double total = 0;
        var confirmed = true;

        // (a) Mute + PauseInstruction while narration audio is playing.
        {
            var session = factory.Create(route, config);
            var observer = new Observer(session);
            try
            {
                var started = await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
                AppendClientModeInstruction(session, started);
                session.AppendInstructions(PromptBuilder.SlideInstruction(0, 1, deck.SlideTitle, deck.Narration), "probe-slide-1");
                var first = await observer.WaitForVoicedAfterAsync(0, 20_000, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("no narration audio within 20 s");
                await Task.Delay(1_500, cancellationToken).ConfigureAwait(false);
                var muteAt = observer.Now;
                session.Mute();
                session.AppendInstructions(PromptBuilder.PauseInstruction(), "pause-1");
                await Task.Delay(10_000, cancellationToken).ConfigureAwait(false);
                var close = await session.CloseAsync().ConfigureAwait(false);
                var usage = close.Seconds ?? observer.LastUsage;
                total += usage ?? 0;
                confirmed &= usage is not null;
                var events = observer.Snapshot();
                var after = events.Where(e => e.Kind == EventKind.Voiced && e.At >= muteAt).ToList();
                await output.WriteLineAsync(
                    $"interrupt (narration): muted {muteAt - first} ms after the first narration audio; {Seconds(VoicedMs(events, 0, muteAt))} voiced before the mute; " +
                    $"{Seconds(after.Sum(e => e.VoicedMs))} voiced audio arrived after it, the last {(after.Count == 0 ? "-" : $"{after[^1].At - muteAt} ms")} after the mute; usage.seconds={FormatUsage(usage)}").ConfigureAwait(false);
                await output.WriteLineAsync($"  assistant text after the mute: \"{string.Concat(events.Where(e => e.Kind == EventKind.Assistant && e.At >= muteAt).Select(e => e.Text)).Trim()}\"").ConfigureAwait(false);
            }
            finally
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        // (b) Mute + PauseInstruction while a delegation is active.
        {
            var session = factory.Create(route, config);
            var observer = new Observer(session) { AnswerClientDelegations = true };
            try
            {
                await ConnectAndNarrateAsync(session, observer, deck, output, cancellationToken).ConfigureAwait(false);
                if (questionIsPart1)
                {
                    await output.WriteLineAsync("  note: no --absent WAV; the part-1 question is asked, which the deck covers, so a delegation may not start").ConfigureAwait(false);
                }

                var askedAt = observer.Now;
                await SendPacedAsync(session, question, cancellationToken).ConfigureAwait(false);
                var delegation = await observer.WaitForAsync(e => e.Kind == EventKind.Delegation && e.At >= askedAt, 20_000, cancellationToken).ConfigureAwait(false);
                if (delegation is null)
                {
                    await output.WriteLineAsync("interrupt (delegation): no delegation started within 20 s of the question; nothing to observe").ConfigureAwait(false);
                }

                var muteAt = observer.Now;
                session.Mute();
                session.AppendInstructions(PromptBuilder.PauseInstruction(), "pause-1");
                await Task.Delay(20_000, cancellationToken).ConfigureAwait(false);
                var close = await session.CloseAsync().ConfigureAwait(false);
                var usage = close.Seconds ?? observer.LastUsage;
                total += usage ?? 0;
                confirmed &= usage is not null;
                var events = observer.Snapshot();
                var after = events.Where(e => e.At >= muteAt).ToList();
                var voiced = after.Where(e => e.Kind == EventKind.Voiced).ToList();
                if (delegation is not null)
                {
                    await output.WriteLineAsync(
                        $"interrupt (delegation): delegation {delegation.Text} at +{delegation.At - askedAt} ms after the question began; muted {muteAt - delegation.At} ms later; " +
                        $"{Seconds(voiced.Sum(e => e.VoicedMs))} voiced audio after the mute{(voiced.Count == 0 ? string.Empty : $", the last {voiced[^1].At - muteAt} ms after it")}; " +
                        $"usage.seconds={FormatUsage(usage)}").ConfigureAwait(false);
                }

                foreach (var e in after.Where(e => e.Kind is EventKind.DelegationDone or EventKind.Tool or EventKind.Delegation or EventKind.Error))
                {
                    await output.WriteLineAsync($"  after the mute +{e.At - muteAt} ms: {e.Kind.ToString().ToLowerInvariant()} {e.Text}").ConfigureAwait(false);
                }

                await output.WriteLineAsync($"  assistant text after the mute: \"{string.Concat(after.Where(e => e.Kind == EventKind.Assistant).Select(e => e.Text)).Trim()}\"").ConfigureAwait(false);
            }
            finally
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        await output.WriteLineAsync($"usage.seconds={(confirmed ? total.ToString("0.###", CultureInfo.InvariantCulture) : $"{total.ToString("0.###", CultureInfo.InvariantCulture)} (partly unconfirmed)")}").ConfigureAwait(false);
        return 0;
    }

    private static async Task ConnectAndNarrateAsync(ILiveSession session, Observer observer, ProbeDeck deck, TextWriter output, CancellationToken cancellationToken)
    {
        var started = await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"session.started: id={started.Id ?? "unknown"} delegation={started.DelegationMode} (+{observer.Now} ms)").ConfigureAwait(false);
        AppendClientModeInstruction(session, started);
        if (session.AppendInstructions(PromptBuilder.SlideInstruction(0, 1, deck.SlideTitle, deck.Narration), "probe-slide-1") is null)
        {
            throw new InvalidOperationException("could not append the slide instruction");
        }

        var first = await observer.WaitForVoicedAfterAsync(0, 20_000, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("no narration audio within 20 s");
        var end = await observer.WaitForAnswerEndAsync(first, QuietAfterNarrationMs, first + 90_000, new HashSet<string>(), cancellationToken).ConfigureAwait(false);
        var events = observer.Snapshot();
        await output.WriteLineAsync(
            $"narration: {Seconds(VoicedMs(events, first, (end ?? observer.Now) + 1))} voiced, first audio +{first} ms; " +
            $"\"{string.Concat(events.Where(e => e.Kind == EventKind.Assistant).Select(e => e.Text)).Trim()}\"").ConfigureAwait(false);
    }

    private static void AppendClientModeInstruction(ILiveSession session, LiveSessionInfo started)
    {
        // The presenter adds the client-mode controls instruction when the session has no delegation backend.
        if (started.DelegationMode == "client")
        {
            session.AppendInstructions(PromptBuilder.ClientModeInstruction(), "client-mode-controls");
        }
    }

    private static async Task SendPacedAsync(ILiveSession session, ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        for (var (offset, frame) = (0, 1); offset < pcm.Length; offset += FrameBytes, frame++)
        {
            session.SendAudio(pcm.Slice(offset, Math.Min(FrameBytes, pcm.Length - offset)));
            var wait = frame * AskRecorder.WindowMs - clock.ElapsedMilliseconds;
            if (wait > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(wait), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static async Task<byte[]?> LoadWavAsync(string path, string option, TextWriter error, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"{option}: cannot read {path}: {exception.Message}").ConfigureAwait(false);
            return null;
        }

        var problem = PcmWav.TryReadPcm16Mono24k(bytes, out var pcm);
        if (problem is not null)
        {
            await error.WriteLineAsync($"{option}: {path} is not 24 kHz mono PCM16 ({problem}). {WavHint}").ConfigureAwait(false);
            return null;
        }

        return pcm;
    }

    private static byte[] Noise(double seconds)
    {
        // Uniform integers in [-35, 35] have an RMS of about 20.5: audible room noise, well under the voice threshold.
        var random = new Random(11);
        var bytes = new byte[(int)Math.Round(seconds * 1000) * BytesPerMs];
        for (var index = 0; index < bytes.Length / 2; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * 2), (short)random.Next(-35, 36));
        }

        return bytes;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    private static long VoicedMs(IReadOnlyList<ProbeEvent> events, long from, long to) =>
        (long)events.Where(e => e.Kind == EventKind.Voiced && e.At >= from && e.At < to).Sum(e => e.VoicedMs);

    internal static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character) || character is '.' or ',' ? character : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Lower case, no diacritics, letters and digits only; a '.' or ',' survives only between two digits (4.2, 4,2).
    /// So case, spacing and punctuation never hide a keyword.
    /// </summary>
    internal static string Canonical(string text)
    {
        var normalized = Normalize(text);
        var builder = new StringBuilder(normalized.Length);
        for (var index = 0; index < normalized.Length; index++)
        {
            var character = normalized[index];
            if (char.IsLetterOrDigit(character)
                || (character is '.' or ',' && index > 0 && index + 1 < normalized.Length
                    && char.IsDigit(normalized[index - 1]) && char.IsDigit(normalized[index + 1])))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    /// <summary>Position of the first keyword in the canonical text (<see cref="Canonical"/>), or -1.</summary>
    internal static int Find(string text, IEnumerable<string> keywords)
    {
        var haystack = Canonical(text);
        var found = keywords
            .Select(keyword => haystack.IndexOf(Canonical(keyword), StringComparison.Ordinal))
            .Where(index => index >= 0)
            .ToList();
        return found.Count == 0 ? -1 : found.Min();
    }

    /// <summary>The sentence of the answer that holds one of the keywords, or null.</summary>
    internal static string? Quote(string text, IEnumerable<string> keywords)
    {
        var keywordList = keywords.ToList();
        if (Find(text, keywordList) < 0)
        {
            return null;
        }

        var sentences = System.Text.RegularExpressions.Regex.Split(text, @"(?<=[.!?])\s+");
        return sentences.FirstOrDefault(sentence => Find(sentence, keywordList) >= 0)?.Trim() ?? text.Trim();
    }

    private static string Seconds(double ms) => (ms / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s";

    private static string Ms(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "null";

    private static string FormatUsage(double? value) =>
        value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unconfirmed";

    internal sealed record ProbeRun(
        ProbeDeck Deck,
        long MuteAt,
        long MuteControlEnd,
        long AskDoneAt,
        long LastQueuedAt,
        long EndMarkAt,
        long BurstStartMs,
        long BurstEndMs,
        long? ContinueAt,
        long? AnswerStart,
        long? AnswerEnd,
        long? ReplyAt,
        long? ReplyMarkMs,
        long SendDurationMs,
        AskRecorderStats Stats,
        bool Trace,
        bool SignalEnd,
        IReadOnlySet<string> EndSignalTypes);

    internal enum EventKind
    {
        Voiced,
        User,
        Assistant,
        Delegation,
        DelegationDone,
        Tool,
        Error,
        Raw,
        Mark
    }

    internal sealed record ProbeEvent(long At, EventKind Kind, string Text, long? StartMs = null, long? EndMs = null, double VoicedMs = 0, string? Type = null);

    /// <summary>Collects session events on a probe-relative clock (ms). Thread-safe; holds no audio bytes.</summary>
    private sealed class Observer
    {
        private readonly object _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<ProbeEvent> _events = [];
        private readonly Dictionary<string, (long SentMs, long At)> _marks = [];
        private readonly HashSet<string> _pendingBackend = [];
        private readonly ILiveSession _session;
        private double? _usage;

        public Observer(ILiveSession session)
        {
            _session = session;
            session.Audio += (bytes, _, _) =>
            {
                if (AudioLevel.IsVoiced(bytes.Span))
                {
                    Add(new ProbeEvent(Now, EventKind.Voiced, string.Empty, VoicedMs: bytes.Length / (double)BytesPerMs));
                }
            };
            session.Transcript += (role, delta, start, end) =>
                Add(new ProbeEvent(Now, role == "user" ? EventKind.User : EventKind.Assistant, delta, start, end));
            session.Delegation += OnDelegation;
            session.DelegatedResponseFinished += (id, type) =>
            {
                lock (_gate)
                {
                    _pendingBackend.Remove(id);
                }

                Add(new ProbeEvent(Now, EventKind.DelegationDone, $"{id} {type}"));
            };
            session.ToolCallRequested += (delegationId, callId, name, _) =>
                Add(new ProbeEvent(Now, EventKind.Tool, $"{name} (delegation {delegationId}, call {callId})"));
            session.UpstreamError += error =>
            {
                var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var c) ? c.ToString() : "unknown";
                var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) ? m.ToString() : string.Empty;
                if (code == "backend_error")
                {
                    lock (_gate)
                    {
                        _pendingBackend.Clear();
                    }
                }

                Add(new ProbeEvent(Now, EventKind.Error, $"{code} {message}".Trim()));
            };
            session.Usage += (seconds, _) =>
            {
                lock (_gate)
                {
                    _usage = seconds;
                }
            };
            session.InputPositionMarked += (id, sentMs) =>
            {
                lock (_gate)
                {
                    _marks[id] = (sentMs, Now);
                }

                Add(new ProbeEvent(Now, EventKind.Mark, $"{id} at input {sentMs} ms"));
            };
            if (session is LiveSession live)
            {
                // Everything else the upstream sends (lifecycle, thinking, delegation envelopes, usage, ...) for the trace
                // and the end-of-response signal; transcript deltas are already recorded above with their start/end ms.
                live.EventReceived += (type, message) =>
                {
                    if (type is "session.input_transcript.delta" or "session.output_transcript.delta")
                    {
                        return;
                    }

                    Add(new ProbeEvent(Now, EventKind.Raw, Summarize(message), Type: type));
                };
            }
        }

        public long Now => _clock.ElapsedMilliseconds;

        /// <summary>Mirrors the presenter while it presents: a client delegation gets the answer-now instruction.</summary>
        public bool AnswerClientDelegations { get; set; }

        public double? LastUsage
        {
            get
            {
                lock (_gate)
                {
                    return _usage;
                }
            }
        }

        public IReadOnlyList<ProbeEvent> Snapshot()
        {
            lock (_gate)
            {
                return [.. _events.OrderBy(e => e.At)];
            }
        }

        public long? FirstVoicedAfter(long at)
        {
            lock (_gate)
            {
                return _events.FirstOrDefault(e => e.Kind == EventKind.Voiced && e.At >= at)?.At;
            }
        }

        public async Task<long?> WaitForVoicedAfterAsync(long at, int timeoutMs, CancellationToken cancellationToken) =>
            (await WaitForAsync(e => e.Kind == EventKind.Voiced && e.At >= at, timeoutMs, cancellationToken).ConfigureAwait(false))?.At;

        public async Task<ProbeEvent?> WaitForAsync(Func<ProbeEvent, bool> match, int timeoutMs, CancellationToken cancellationToken)
        {
            var until = Now + timeoutMs;
            while (true)
            {
                lock (_gate)
                {
                    var found = _events.FirstOrDefault(match);
                    if (found is not null)
                    {
                        return found;
                    }
                }

                if (Now >= until)
                {
                    return null;
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Upstream event types seen so far that <see cref="IsEndOfResponseType"/> accepts.</summary>
        public IReadOnlySet<string> EndSignalTypes()
        {
            lock (_gate)
            {
                return _events.Where(e => e.Kind == EventKind.Raw && e.Type is not null && IsEndOfResponseType(e.Type))
                    .Select(e => e.Type!).ToHashSet(StringComparer.Ordinal);
            }
        }

        /// <summary>
        /// The end of the response that started at <paramref name="from"/>, once no backend delegation is pending. With
        /// end-of-response types: the latest such event after <paramref name="from"/> once no assistant output followed
        /// it for 500 ms. Without: the last assistant output (voiced audio or transcript) once <paramref name="quietMs"/>
        /// passed without any. Null at the cap.
        /// </summary>
        public async Task<long?> WaitForAnswerEndAsync(long from, int quietMs, long capAt, IReadOnlySet<string> endTypes, CancellationToken cancellationToken)
        {
            while (Now < capAt)
            {
                lock (_gate)
                {
                    var last = _events.LastOrDefault(e => e.At >= from && (e.Kind == EventKind.Voiced || (e.Kind == EventKind.Assistant && e.Text.Length > 0)))?.At ?? from;
                    if (_pendingBackend.Count == 0)
                    {
                        if (endTypes.Count > 0)
                        {
                            var signal = _events.LastOrDefault(e => e.Kind == EventKind.Raw && e.At >= from && endTypes.Contains(e.Type ?? string.Empty));
                            if (signal is not null && signal.At >= last && Now - signal.At >= EndSignalSettleMs)
                            {
                                return signal.At;
                            }
                        }
                        else if (Now - last >= quietMs)
                        {
                            return last;
                        }
                    }
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

        public async Task<IReadOnlyDictionary<string, (long SentMs, long At)>> WaitForMarksAsync(
            IReadOnlyList<string> ids, int timeoutMs, CancellationToken cancellationToken)
        {
            var until = Now + timeoutMs;
            while (true)
            {
                lock (_gate)
                {
                    if (ids.All(_marks.ContainsKey))
                    {
                        return ids.ToDictionary(id => id, id => _marks[id]);
                    }
                }

                if (Now >= until)
                {
                    throw new TimeoutException("input-position marks did not come back from the send loop");
                }

                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }

        private void OnDelegation(JsonElement raw)
        {
            var payload = raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("delegation", out var nested) ? nested : default;
            var target = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("target", out var t) ? t.ToString() : "unknown";
            var id = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("id", out var i) ? i.ToString() : null;
            if (target == "responses" && id is not null)
            {
                lock (_gate)
                {
                    _pendingBackend.Add(id);
                }
            }

            Add(new ProbeEvent(Now, EventKind.Delegation, $"{target} {id ?? "(no id)"}"));
            if (target == "client" && AnswerClientDelegations)
            {
                _session.AppendInstructions(PromptBuilder.ClientDelegationAnswerNowInstruction(), $"question-{id ?? "client"}-answer-now", id);
            }
        }

        private void Add(ProbeEvent e)
        {
            lock (_gate)
            {
                _events.Add(e);
            }
        }
    }
}

/// <summary>The one-slide probe deck (plan 011 T1): two independent, scorable facts and the two-part question.</summary>
internal sealed record ProbeDeck(
    string Title,
    string SlideTitle,
    string Narration,
    string QuestionPart1,
    string QuestionPart2,
    IReadOnlyList<string> Part1Keywords,
    IReadOnlyList<string> Part2Keywords,
    IReadOnlyList<string> FactA,
    IReadOnlyList<string> FactB)
{
    public static ProbeDeck For(string lang) => lang == "vi" ? Vietnamese : English;

    public static readonly ProbeDeck English = new(
        "Programme review",
        "First-year results",
        "In its first year the programme moved the Da Nang office to paperless contracts. The Hanoi expansion cost 4.2 billion dong.",
        "What did the programme change at the Da Nang office in its first year,",
        "and how much did the Hanoi expansion cost?",
        ["Da Nang", "Danang"],
        ["Hanoi", "Ha Noi"],
        ["paperless", "paper-less", "paper free"],
        ["4.2", "4,2", "four point two"]);

    public static readonly ProbeDeck Vietnamese = new(
        "Đánh giá chương trình",
        "Kết quả năm đầu tiên",
        "Trong năm đầu tiên, chương trình đã chuyển văn phòng Đà Nẵng sang hợp đồng không giấy. Việc mở rộng tại Hà Nội tốn 4,2 tỷ đồng.",
        "Chương trình đã thay đổi gì ở văn phòng Đà Nẵng trong năm đầu tiên,",
        "và việc mở rộng ở Hà Nội tốn bao nhiêu tiền?",
        ["Đà Nẵng", "Da Nang", "Danang"],
        ["Hà Nội", "Hanoi"],
        ["không giấy", "không dùng giấy", "điện tử", "paperless"],
        ["4,2", "4.2", "bốn phẩy hai", "bốn tỷ hai", "4 tỷ 2", "bốn điểm hai", "4200 triệu", "4.200 triệu"]);
}

/// <summary>Reads the PCM payload of a RIFF/WAVE file that is 24 kHz, mono, 16-bit PCM.</summary>
internal static class PcmWav
{
    public static string? TryReadPcm16Mono24k(byte[] file, out byte[] pcm)
    {
        pcm = [];
        var span = file.AsSpan();
        if (span.Length < 12 || !span[..4].SequenceEqual("RIFF"u8) || !span[8..12].SequenceEqual("WAVE"u8))
        {
            return "not a RIFF/WAVE file";
        }

        int? format = null, channels = null, rate = null, bits = null;
        var offset = 12;
        while (offset + 8 <= span.Length)
        {
            var id = span.Slice(offset, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset + 4, 4));
            var body = offset + 8;
            if (size < 0 || body + size > span.Length)
            {
                size = span.Length - body;
            }

            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(body + 2, 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(body + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(body + 14, 2));
                if (format == 0xFFFE && size >= 26)
                {
                    // WAVE_FORMAT_EXTENSIBLE: the sub-format GUID starts with the real format tag.
                    format = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(body + 24, 2));
                }
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (format != 1 || channels != 1 || rate != 24_000 || bits != 16)
                {
                    return format is null
                        ? "no fmt chunk before the data"
                        : $"format {format}, {channels} channel(s), {rate} Hz, {bits}-bit";
                }

                pcm = span.Slice(body, size & ~1).ToArray();
                return pcm.Length == 0 ? "no audio data" : null;
            }

            offset = body + size + (size & 1);
        }

        return "no data chunk";
    }
}
