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
    private const int ReplyObserveMs = 10_000;
    private const int ProvenanceSlackMs = 250;
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
                answerEnd = await observer.WaitForAnswerEndAsync(answerStart.Value, AnswerEndQuietMs, answerStart.Value + AnswerMaxMs, cancellationToken).ConfigureAwait(false);
                var windowEnd = (answerEnd ?? observer.Now) + SecondResponseWindowMs;
                while (observer.Now < windowEnd)
                {
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
                continueAt, answerStart, answerEnd, replyAt, replyMarkMs, sendDurationMs, stats);
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

    private static async Task<bool> ReportAsync(ProbeRun run, IReadOnlyList<ProbeEvent> events, TextWriter output)
    {
        var deck = run.Deck;
        var errors = events.Where(e => e.Kind == EventKind.Error).ToList();
        foreach (var e in errors)
        {
            await output.WriteLineAsync($"upstream error +{e.At} ms: {e.Text}").ConfigureAwait(false);
        }

        // (ii) mute control, (iii) nothing voiced before the last chunk was queued.
        var muteVoicedMs = VoicedMs(events, run.MuteAt, run.MuteControlEnd);
        var muteUserDeltas = events.Count(e => e.Kind == EventKind.User && e.At >= run.MuteAt && e.At < run.MuteControlEnd && e.Text.Trim().Length > 0);
        var earlyVoicedMs = VoicedMs(events, run.MuteAt, run.LastQueuedAt);

        // (iv) user turns after the burst, separated by any assistant output.
        var observeEnd = run.ReplyAt ?? long.MaxValue;
        var turns = new List<List<ProbeEvent>>();
        List<ProbeEvent>? current = null;
        foreach (var e in events.Where(e => e.At >= run.AskDoneAt && e.At < observeEnd))
        {
            if (e.Kind == EventKind.User && e.Text.Trim().Length > 0)
            {
                current ??= [];
                if (current.Count == 0)
                {
                    turns.Add(current);
                }

                current.Add(e);
            }
            else if (e.Kind is EventKind.Voiced or EventKind.Assistant or EventKind.Delegation or EventKind.Tool)
            {
                current = null;
            }
        }

        var burstDeltas = turns.SelectMany(turn => turn).ToList();
        var singleTurnText = turns.Count == 1 ? string.Concat(turns[0].Select(e => e.Text)) : string.Empty;
        var part1At = Find(singleTurnText, deck.Part1Keywords);
        var part2At = Find(singleTurnText, deck.Part2Keywords);

        // (v) the answer's transcript; (vi) no second response within 15 s after it.
        var secondResponseAt = run.AnswerEnd is { } end
            ? events.FirstOrDefault(e => e.Kind == EventKind.Voiced && e.At > end && e.At <= end + SecondResponseWindowMs)?.At
            : null;
        var answerText = run.AnswerStart is null
            ? string.Empty
            : string.Concat(events
                .Where(e => e.Kind == EventKind.Assistant && e.At >= run.AskDoneAt
                    && e.At < Math.Min(secondResponseAt ?? long.MaxValue, (run.AnswerEnd ?? long.MaxValue - 2_000) + 1_500))
                .Select(e => e.Text));
        var factA = Quote(answerText, deck.FactA);
        var factB = Quote(answerText, deck.FactB);

        await output.WriteLineAsync("user turns after the burst:").ConfigureAwait(false);
        for (var index = 0; index < turns.Count; index++)
        {
            await output.WriteLineAsync($"  turn {index + 1}: \"{string.Concat(turns[index].Select(e => e.Text)).Trim()}\"").ConfigureAwait(false);
        }

        await output.WriteLineAsync($"answer: \"{answerText.Trim()}\"").ConfigureAwait(false);
        foreach (var e in events.Where(e => e.Kind is EventKind.Delegation or EventKind.DelegationDone or EventKind.Tool && e.At >= run.MuteAt))
        {
            await output.WriteLineAsync($"  {e.Kind.ToString().ToLowerInvariant()} +{e.At - run.AskDoneAt} ms after Ask done: {e.Text}").ConfigureAwait(false);
        }

        // Latencies (the answer-start budget comes from Ask done → first answer audio).
        if (run.AnswerStart is { } start)
        {
            await output.WriteLineAsync(
                $"latency: Ask done -> first answer audio {start - run.AskDoneAt} ms; last chunk queued -> {start - run.LastQueuedAt} ms; " +
                $"end mark on the wire -> {start - run.EndMarkAt} ms; answer {(run.AnswerEnd is { } e2 ? $"ended +{e2 - run.AskDoneAt} ms, {Seconds(VoicedMs(events, start, e2 + 1))} voiced" : "did not end within the cap")}").ConfigureAwait(false);
            var lastBurstDelta = burstDeltas.LastOrDefault();
            if (lastBurstDelta is not null)
            {
                await output.WriteLineAsync(
                    $"late transcript (R5): last burst delta arrived {lastBurstDelta.At - start} ms after answer start" +
                    (run.AnswerEnd is { } e3 ? $", {lastBurstDelta.At - e3} ms after answer end" : string.Empty)).ConfigureAwait(false);
            }
        }
        else
        {
            await output.WriteLineAsync($"latency: no answer audio within {AnswerStartWaitMs / 1000} s after the last chunk").ConfigureAwait(false);
        }

        // Provenance (P-13): burst deltas start before the burst end on the input clock; the reply starts after it.
        await output.WriteLineAsync($"provenance: burst range [{run.BurstStartMs}, {run.BurstEndMs}] ms on the input clock (kept audio starts about {AskRecorder.LeadMs} ms after the start mark)").ConfigureAwait(false);
        var burstOk = burstDeltas.Count > 0;
        foreach (var e in burstDeltas)
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

        var criteria = new (string Name, bool Pass, string Detail)[]
        {
            ("(i) no upstream error", errors.Count == 0, $"{errors.Count} errors"),
            ("(ii) mute control silent", muteVoicedMs == 0 && muteUserDeltas == 0, $"{muteVoicedMs} ms voiced assistant audio, {muteUserDeltas} user deltas while muted"),
            ("(iii) nothing voiced before the last chunk was queued", earlyVoicedMs == 0, $"{earlyVoicedMs} ms voiced"),
            ("(iv) exactly one user turn, part 1 before part 2", turns.Count == 1 && part1At >= 0 && part2At > part1At,
                $"{turns.Count} turns; part-1 keyword {(part1At >= 0 ? "found" : "missing")}, part-2 keyword {(part2At >= 0 ? "found" : "missing")}{(part1At >= 0 && part2At >= 0 && part2At < part1At ? " (out of order)" : string.Empty)}"),
            ("(v) one answer with fact A and fact B", run.AnswerStart is not null && factA is not null && factB is not null,
                $"fact A {(factA is null ? "missing" : $"\"{factA}\"")}; fact B {(factB is null ? "missing" : $"\"{factB}\"")}"),
            ("(vi) no second unsolicited response within 15 s", run.AnswerEnd is not null && secondResponseAt is null,
                run.AnswerEnd is null ? "the answer did not end" : secondResponseAt is null ? "none" : $"voiced audio {secondResponseAt - run.AnswerEnd} ms after the answer ended")
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
        var end = await observer.WaitForAnswerEndAsync(first, QuietAfterNarrationMs, first + 90_000, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Index of the first keyword in the space-free normalised text, or -1.</summary>
    internal static int Find(string text, IEnumerable<string> keywords)
    {
        var haystack = Normalize(text).Replace(" ", string.Empty, StringComparison.Ordinal);
        var found = keywords
            .Select(keyword => haystack.IndexOf(Normalize(keyword).Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal))
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

    private sealed record ProbeRun(
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
        AskRecorderStats Stats);

    private enum EventKind
    {
        Voiced,
        User,
        Assistant,
        Delegation,
        DelegationDone,
        Tool,
        Error
    }

    private sealed record ProbeEvent(long At, EventKind Kind, string Text, long? StartMs = null, long? EndMs = null, double VoicedMs = 0);

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
            };
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

        /// <summary>The last voiced audio after <paramref name="from"/> once it is followed by <paramref name="quietMs"/> of quiet and no backend delegation is pending; null at the cap.</summary>
        public async Task<long?> WaitForAnswerEndAsync(long from, int quietMs, long capAt, CancellationToken cancellationToken)
        {
            while (Now < capAt)
            {
                lock (_gate)
                {
                    var last = _events.LastOrDefault(e => e.Kind == EventKind.Voiced && e.At >= from)?.At ?? from;
                    if (Now - last >= quietMs && _pendingBackend.Count == 0)
                    {
                        return last;
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
