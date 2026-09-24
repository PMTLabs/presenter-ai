using System.Text.Json;
using FluentAssertions;
using PresenterAi.Application.Presenting.Asking;
using PresenterAi.Cli;
using Xunit;
using static PresenterAi.Cli.AskProbeCommand;

namespace PresenterAi.Cli.Tests;

/// <summary>Plan 011 T1: the ask-probe's turn, keyword and answer scoring, replayed on the live run-1 timeline.</summary>
public sealed class AskProbeScoringTests
{
    private const long AskDone = 10_000;

    // Live run 1 (English, vad): the burst transcript as it arrived (ms after Ask done, start_ms, end_ms, text).
    private static readonly (long At, long Start, string Text)[] Run1Burst =
    [
        (384, 20000, " Did the"), (543, 20600, " programme"), (700, 21000, " change at"), (774, 21200, " the"),
        (844, 21400, " Da"), (885, 21600, " Nang"), (1059, 22000, " office"), (1101, 22200, " in its"),
        (1204, 22600, " first"), (1318, 23000, " year"), (1478, 23400, ", and"), (1551, 23600, " how"),
        (1624, 23800, " much"), (1701, 24000, " did"), (1736, 24200, " the"), (1856, 24600, " Hanoi"),
        (2102, 25200, " expansion"), (2393, 25800, " cost")
    ];

    [Fact]
    public async Task Early_assistant_transcript_does_not_split_a_burst_turn_and_every_segment_is_printed()
    {
        var events = Run1Events();
        var output = new StringWriter();

        var passed = await ReportAsync(Run(answerStart: 2_600, answerEnd: 3_000), events, output);

        var text = output.ToString();
        passed.Should().BeFalse("the live answer was split");
        text.Should().Contain("burst turn: \"Did the programme change at the Da Nang office in its first year, and how much did the Hanoi expansion cost\"")
            .And.NotContain("other turn");
        text.Should().Contain("PASS (iv) one question turn, part 1 before part 2, no new utterance before the reply: 18 deltas in one turn; part-1 keyword before part-2 keyword")
            .And.Contain("PASS (vii) question transcript finished before the answer ended: margin +607 ms")
            .And.Contain("PASS (reply) the reply is a new utterance");
        text.Should().Contain("[answer]: \"It did. It\"")
            .And.Contain("+7739..+8339 ms after Ask done, 0.7 s voiced: \"moved to paperless contracts.\"");
        text.Should().Contain("FAIL (vi) no second unsolicited response within 15 s: a new response started 4739 ms after the answer ended");
        text.Should().Contain("FAIL (v) one answer with fact A and fact B: fact A missing; fact B missing");
    }

    [Fact]
    public async Task A_new_utterance_after_the_answer_and_before_the_reply_fails_iv()
    {
        var events = Run1Events();
        events.Add(new ProbeEvent(AskDone + 20_000, EventKind.User, " Thanks", 60_000, 60_200));

        var output = new StringWriter();
        await ReportAsync(Run(answerStart: 2_600, answerEnd: 3_000), Sorted(events), output);

        output.ToString().Should().Contain("FAIL (iv) one question turn, part 1 before part 2, no new utterance before the reply: 1 new utterance(s) before the reply (first at +20000 ms: \"Thanks\")")
            .And.Contain("(iv) start_ms range diagnostic (not scored): 1 user deltas outside the burst range before the reply (start_ms 60000)");
    }

    [Fact]
    public async Task A_late_question_delta_after_the_answer_ended_fails_vii()
    {
        // " cost" arrives 1 s after the previous question delta but after the answer ended: still the question, too late.
        var events = Run1Events().Where(e => e.Text != " cost").ToList();
        events.Add(new ProbeEvent(AskDone + 3_050, EventKind.User, " cost", 25_800, 26_000));

        var output = new StringWriter();
        await ReportAsync(Run(answerStart: 2_600, answerEnd: 3_000), Sorted(events), output);

        output.ToString().Should().Contain("FAIL (vii) question transcript finished before the answer ended: margin -50 ms")
            .And.Contain("PASS (iv)");
    }

    [Fact]
    public async Task A_reply_less_than_1_5_s_after_a_question_delta_is_not_a_new_utterance()
    {
        var events = Run1Events().Where(e => e.Text != " Yes").ToList();
        events.Add(new ProbeEvent(AskDone + 40_000 - 500, EventKind.User, " and", 30_000, 30_200));
        events.Add(new ProbeEvent(AskDone + 40_700, EventKind.User, " Yes", 30_400, 30_600));

        var output = new StringWriter();
        await ReportAsync(Run(answerStart: 2_600, answerEnd: 3_000), Sorted(events), output);

        output.ToString().Should().Contain("FAIL (reply) the reply is a new utterance: first reply delta +40700 ms after Ask done, 1200 ms after the previous user delta");
    }

    [Theory]
    [InlineData("01-en-1")]
    [InlineData("05-cap24-1")]
    [InlineData("06-cap24-2")]
    public async Task Suite_20260924_144029_replays_pass_iv_vii_and_the_reply_check(string name)
    {
        var (user, assistant, voiced, burstStart, burstEnd, replyAt) = name switch
        {
            "01-en-1" => (En1User, En1Assistant, En1Voiced, En1BurstStart, En1BurstEnd, En1ReplyAt),
            "05-cap24-1" => (Cap24FirstUser, Cap24FirstAssistant, Cap24FirstVoiced, Cap24FirstBurstStart, Cap24FirstBurstEnd, Cap24FirstReplyAt),
            _ => (Cap24SecondUser, Cap24SecondAssistant, Cap24SecondVoiced, Cap24SecondBurstStart, Cap24SecondBurstEnd, Cap24SecondReplyAt)
        };
        var events = user.Select(d => new ProbeEvent(AskDone + d.At, EventKind.User, d.Text, d.Start, d.Start + 200))
            .Concat(assistant.Select(d => new ProbeEvent(AskDone + d.At, EventKind.Assistant, d.Text, d.Start, d.Start + 200)))
            .Concat(voiced.SelectMany(v => Enumerable.Range(0, (int)((v.Stop - v.Start) / 100) + 1)
                .Select(i => new ProbeEvent(AskDone + v.Start + i * 100, EventKind.Voiced, string.Empty, VoicedMs: 100))))
            .ToList();
        var run = Run(answerStart: voiced[0].Start, answerEnd: voiced[^1].Stop) with
        {
            BurstStartMs = burstStart, BurstEndMs = burstEnd, ReplyAt = AskDone + replyAt, LastQueuedAt = AskDone, EndMarkAt = AskDone
        };

        var output = new StringWriter();
        await ReportAsync(run, Sorted(events), output);

        var text = output.ToString();
        text.Should().Contain("PASS (iv) one question turn", name).And.Contain("PASS (vii)", name).And.Contain("PASS (reply)", name);
    }

    [Fact]
    public async Task Assistant_audio_between_the_two_halves_is_a_diagnostic_not_a_failure()
    {
        var events = Run1Events();
        events.Add(new ProbeEvent(AskDone + 1_000, EventKind.Voiced, string.Empty, VoicedMs: 100));

        var output = new StringWriter();
        await ReportAsync(Run(answerStart: 2_600, answerEnd: 3_000), Sorted(events), output);

        output.ToString().Should().Contain("PASS (iv)")
            .And.Contain("(iv) diagnostic (not scored): 0 other user turns by arrival; 1 assistant/delegation events arrived between the part-1 and part-2 keywords");
    }

    [Fact]
    public async Task Missing_keyword_is_reported_as_the_reason()
    {
        var events = Run1Events().Where(e => e.Text != " Hanoi").ToList();

        var output = new StringWriter();
        await ReportAsync(Run(answerStart: 2_600, answerEnd: 3_000), events, output);

        output.ToString().Should().Contain("FAIL (iv) one question turn, part 1 before part 2, no new utterance before the reply: part-2 keyword missing");
    }

    // Live runs en-3, en-4 and raw: the model began answering while the burst transcript was still arriving (and, raw,
    // answered part 1 before part 2 was transcribed). Every user delta is in the burst range, so (iv) passes.
    // Live runs en-3, en-4 and raw: the model began answering while the burst transcript was still arriving (and, raw,
    // answered part 1 before part 2 was transcribed). Every user delta is in the burst range, so (iv) passes.
    [Fact]
    public Task Live_replay_en3_passes_iv() => AssertReplayPassesIvAsync(
        "en-3",
        [(421, 18000, " What did"), (548, 18200, " the"), (652, 18800, " programme"), (835, 19200, " change at"), (922, 19400, " the"),
             (997, 19600, " Da"), (1056, 19800, " Nang"), (1231, 20200, " office in"), (1276, 20400, " its"), (1528, 20800, " first"),
             (1744, 21200, " year"), (1906, 21600, " And"), (1988, 21800, " how"), (2064, 22000, " much"), (2145, 22200, " did"),
             (2197, 22400, " the"), (2324, 22800, " Hanoi"), (2596, 23400, " expansion"), (2831, 23800, " cost")],
        [(1529, 20800, " It moved"), (1992, null, ""), (2092, null, ""), (2719, 23600, " the Da"), (2833, 23800, " Nang"), (2935, 24000, " office")],
        17180, 24580, 26201, 46000);

    [Fact]
    public Task Live_replay_en4_passes_iv() => AssertReplayPassesIvAsync(
        "en-4",
        [(368, 17800, "What"), (453, 18000, " did"), (501, 18200, " the"), (640, 18600, " programme"), (848, 19000, " change"),
             (940, 19200, " at the"), (1034, 19400, " Da"), (1072, 19600, " Nang"), (1254, 20000, " office"), (1348, 20200, " in"),
             (1388, 20400, " its"), (1650, 20800, " first"), (1762, 21000, " year"), (2008, 21600, " and how"), (2179, 22000, " much"),
             (2208, 22200, " did the"), (2374, 22600, " Hanoi"), (2522, 23200, " expansion"), (2896, 23800, " cost")],
        [(1650, 20800, " It moved"), (1762, 21000, " the"), (2449, null, ""), (2786, 23600, " It moved"), (2896, 23800, " the")],
        17040, 24440, 27587, 47400);

    [Fact]
    public Task Live_replay_raw_passes_iv() => AssertReplayPassesIvAsync(
        "raw",
        [(504, 18200, " Um"), (686, 18600, ", change"), (760, 18800, " at the"), (835, 19000, " Da"), (878, 19200, " Nang"),
             (1029, 19600, " office"), (1098, 19800, " in"), (1175, 20000, " its"), (1217, 20200, " first"), (1338, 20600, " year"),
             (4182, 31600, " And"), (4260, 31800, " how"), (4333, 32000, " much"), (4412, 32200, " did"), (4450, 32400, " the"),
             (4572, 32800, " Hanoi"), (4732, 33400, " expansion"), (5071, 34000, " cost")],
        [(1568, 21000, " Paperless"), (1766, 21400, " contracts."), (2555, null, ""), (3760, null, ""), (4982, 33800, " 4"), (5071, 34000, ".2")],
        16640, 35220, 22547, 43000);

    private static async Task AssertReplayPassesIvAsync(string name, (long At, long Start, string Text)[] user, (long At, long? Start, string Text)[] assistant,
        long burstStart, long burstEnd, long replyAt, long replyStart)
    {
        var events = user.Select(d => new ProbeEvent(AskDone + d.At, EventKind.User, d.Text, d.Start, d.Start + 200))
            .Concat(assistant.Select(d => d.Start is null
                ? new ProbeEvent(AskDone + d.At, EventKind.Voiced, string.Empty, VoicedMs: 100)
                : new ProbeEvent(AskDone + d.At, EventKind.Assistant, d.Text, d.Start, d.Start + 200)))
            .Append(new ProbeEvent(AskDone + replyAt + 1_300, EventKind.User, " Yes", replyStart, replyStart + 200))
            .ToList();
        var run = Run(answerStart: 2_000, answerEnd: 12_000) with { BurstStartMs = burstStart, BurstEndMs = burstEnd, ReplyAt = AskDone + replyAt };

        var output = new StringWriter();
        await ReportAsync(run, Sorted(events), output);

        output.ToString().Should().Contain("PASS (iv) one question turn, part 1 before part 2, no new utterance before the reply", name)
            .And.Contain("truncation: none detected");
    }

    [Fact]
    public async Task Near_cap_reply_well_before_the_end_mark_is_truncated()
    {
        (long At, long Start, string Text)[] user = [(9499, 47600, " me"), (9658, 48000, " give"), (10153, 49600, " on"), (32298, 50000, " why"), (32452, 50200, " this")];
        var events = user.Select(d => new ProbeEvent(AskDone + d.At, EventKind.User, d.Text, d.Start, d.Start + 200))
            .Append(new ProbeEvent(AskDone + 50_263, EventKind.User, " Yes", 55_600, 55_800))
            .ToList();
        var run = Run(answerStart: 40_000, answerEnd: 41_000) with { BurstStartMs = 17_720, BurstEndMs = 125_200, ReplyAt = AskDone + 49_003, ReplyMarkMs = 125_200 };

        var output = new StringWriter();
        await ReportAsync(run, Sorted(events), output);

        output.ToString().Should().Contain("ingested: max user end_ms before the reply 50400 (end mark 125200, -74800 ms); reply start_ms - end mark -69600 ms")
            .And.Contain("truncation: TRUNCATED (the reply starts at 55600 on the input clock, under end mark - 1000 = 124200; about 69.6 s of the burst was not ingested)")
            .And.Contain("result: FAIL TRUNCATED")
            .And.Contain("FAIL (iv) one question turn, part 1 before part 2, no new utterance before the reply: part-1 keyword missing; part-2 keyword missing");
    }

    [Fact]
    public async Task A_whole_answer_with_both_facts_passes_and_the_trace_lists_upstream_events()
    {
        var events = Run1Events().Where(e => e.At < AskDone + 7_000).ToList();
        events.Add(new ProbeEvent(AskDone + 2_700, EventKind.Assistant, " It moved to paperless contracts, and Hanoi cost 4.2 billion dong. Shall I carry on?"));
        events.Add(new ProbeEvent(AskDone + 3_050, EventKind.Raw, "response_id=\"r1\"", Type: "session.output_audio.done"));
        events.Add(new ProbeEvent(AskDone + 41_392, EventKind.User, " Yes", 39_600, 39_800));
        var output = new StringWriter();

        var passed = await ReportAsync(Run(answerStart: 2_600, answerEnd: 3_050, trace: true, endTypes: ["session.output_audio.done"]), Sorted(events), output);

        var text = output.ToString();
        passed.Should().BeTrue(text);
        text.Should().Contain("PASS (v) one answer with fact A and fact B: fact A \"It moved to paperless contracts")
            .And.Contain("(ended by an end-of-response event)")
            .And.Contain("session.output_audio.done [end-of-response] response_id=\"r1\"")
            .And.Contain("voiced audio start")
            .And.Contain("voiced audio stop")
            .And.Contain("user transcript start_ms=25800 end_ms=26000 \" cost\" [burst range]");
    }

    [Fact]
    public async Task Run_2_delivery_stall_on_a_continuous_output_clock_is_one_answer()
    {
        var output = new StringWriter();

        var passed = await ReportAsync(Run2(), Run2Events(), output);

        var text = output.ToString();
        passed.Should().BeTrue(text);
        text.Should().Contain("PASS (iv)").And.Contain("PASS (v)").And.Contain("PASS (vi) no second unsolicited response within 15 s: none");
        text.Split('\n').Count(line => line.StartsWith("  +", StringComparison.Ordinal) && line.Contains("ms after Ask done,", StringComparison.Ordinal))
            .Should().Be(1, "the 3.4 s arrival gap continues on the output clock");
        text.Should().Contain("[answer]: \"It moved the Da Nang office to paperless contracts, and the Hanoi expansion cost 4.2 billion dong.\"")
            .And.Contain("rule: a new response starts only after >= 3 s without assistant output AND an output-clock jump");
    }

    [Theory]
    [InlineData(32_000, false)]
    [InlineData(28_600, true)]
    public async Task Output_after_3_s_quiet_is_a_second_response_only_when_the_output_clock_jumps(long startMs, bool passes)
    {
        var events = Run2Events();
        events.Add(new ProbeEvent(AskDone + 15_500, EventKind.Assistant, " Anything else?", startMs, startMs + 600));
        for (var at = 15_600L; at <= 16_000; at += 100)
        {
            events.Add(new ProbeEvent(AskDone + at, EventKind.Voiced, string.Empty, VoicedMs: 100));
        }

        var output = new StringWriter();
        await ReportAsync(Run2(), Sorted(events), output);

        output.ToString().Should().Contain(passes
            ? "PASS (vi) no second unsolicited response within 15 s: none"
            : "FAIL (vi) no second unsolicited response within 15 s: a new response started 3583 ms after the answer ended");
    }

    [Theory]
    [InlineData("en", "The Hanoi expansion cost 4 point 2 billion dong.")]
    [InlineData("en", "It cost four point two billion dong.")]
    [InlineData("en", "It cost 4.2 billion dong.")]
    [InlineData("en", "It cost 4,2 billion dong.")]
    [InlineData("vi", "Việc mở rộng tại Hà Nội tốn 4,2 tỷ đồng.")]
    [InlineData("vi", "Tốn bốn phẩy hai tỷ đồng.")]
    [InlineData("vi", "Tốn 4 phẩy 2 tỷ đồng.")]
    public void Fact_b_accepts_every_spoken_form_of_4_point_2(string lang, string answer) =>
        AskProbeCommand.Quote(answer, ProbeDeck.For(lang).FactB).Should().NotBeNull();

    [Theory]
    [InlineData("en", "It cost 42 billion dong.")]
    [InlineData("en", "It cost four billion dong.")]
    [InlineData("vi", "Tốn 42 tỷ đồng.")]
    public void Fact_b_rejects_other_amounts(string lang, string answer) =>
        AskProbeCommand.Quote(answer, ProbeDeck.For(lang).FactB).Should().BeNull();

    [Fact]
    public void Mark_timeout_scales_with_the_audio_queued_before_the_mark()
    {
        AskProbeCommand.MarkTimeoutMs(0).Should().Be(10_000);
        AskProbeCommand.MarkTimeoutMs(25_200 * 48).Should().Be(22_000, "a 25.2 s burst is 1.2 MB");
    }

    [Fact]
    public async Task Clock_diagnostic_reports_overrun_offset_and_wire_lag()
    {
        var events = Run2Events();
        events.Add(new ProbeEvent(AskDone + 2_900, EventKind.User, " cost?", 25_000, 25_200));

        var output = new StringWriter();
        await ReportAsync(Run2(), Sorted(events), output);

        output.ToString().Should().Contain("clock: overrun (max user start_ms before the reply - end mark) +560 ms; " +
            "upstream-clock offset estimate (max user end_ms - (end mark - 1000 ms zero tail)) +1760 ms; wire lag 34 ms");
    }

    [Theory]
    [InlineData("DA-NANG office, HA NOI!", true)]
    [InlineData("what changed at da nang... and hanoi?", true)]
    [InlineData("Hanoi first, then Da Nang", false)]
    public void Keyword_search_ignores_case_spacing_and_punctuation_but_keeps_order(string text, bool inOrder)
    {
        var deck = ProbeDeck.English;
        var part1 = Find(text, deck.Part1Keywords);
        var part2 = Find(text, deck.Part2Keywords);

        part1.Should().BeGreaterOrEqualTo(0);
        part2.Should().BeGreaterOrEqualTo(0);
        (part2 > part1).Should().Be(inOrder);
        Find("It cost 42 billion", deck.FactB).Should().Be(-1, "the decimal point of 4.2 is kept");
    }

    [Theory]
    [InlineData("session.output_audio.done", true)]
    [InlineData("session.output_transcript.done", true)]
    [InlineData("response.done", true)]
    [InlineData("session.turn.ended", true)]
    [InlineData("response.event", false)]
    [InlineData("session.input_audio.done", false)]
    [InlineData("session.instructions.appended", false)]
    [InlineData("session.delegation.created", false)]
    public void End_of_response_types(string type, bool expected) => IsEndOfResponseType(type).Should().Be(expected);

    [Fact]
    public void Trace_summary_omits_audio_instructions_and_secret_shaped_fields()
    {
        using var document = JsonDocument.Parse("""
            {"type":"session.started","session":{"id":"s","instructions":"long"},"api_key":"abc","nested":{"token":"t","audio":"AAAA","item":{"type":"message","status":"done"}},"start_ms":5}
            """);

        var summary = Summarize(document.RootElement);

        summary.Should().Contain("session=<omitted>").And.Contain("api_key=<omitted>").And.Contain("nested.token=<omitted>")
            .And.Contain("nested.audio=<omitted>").And.Contain("nested.item.type=\"message\"").And.Contain("start_ms=5")
            .And.NotContain("abc").And.NotContain("AAAA").And.NotContain("long");
    }

    // 01-en-1: burst [17120, 25380], reply sent at +27970.
    private static readonly (long At, long Start, string Text)[] En1User =
    [
        (414, 21200, "What"), (484, 21400, " did"), (527, 21600, " the"), (719, 22200, " programme"), (918, 22800, " change"), (996, 23000, " at the"), (1073, 23200, " Da"), (1117, 23400, " Nang"), (1386, 24200, " office in"), (1448, 24400, " its"), (1570, 24800, " first"), (1696, 25200, " year"), (1851, 25600, "? And"), (2000, 26000, " how"), (2093, 26200, " much"), (2138, 26400, " did the"), (2264, 26800, " Hanoi"), (2390, 27200, " expansion"), (2692, 27800, " cost"), (2796, 28000, "?"), (29366, 52000, " Yes")
    ];
    private static readonly (long At, long Start, string Text)[] En1Assistant =
    [
        (2693, 27800, " It made"), (2798, 28000, " the Da"), (2900, 28200, " Nang"), (8784, 28600, " office"), (8981, 28800, " paperless"), (9112, 29000, ","), (9421, 29600, " and"), (9527, 29800, " the"), (9628, 30000, " Hanoi"), (9823, 30400, " expansion"), (10030, 30800, " cost"), (10253, 31200, " 4"), (10342, 31400, ".2"), (10555, 31800, " billion"), (10968, 32200, " dong.")
    ];
    private static readonly (long Start, long Stop)[] En1Voiced = [(2925, 3425), (8675, 12871)];
    private const long En1BurstStart = 17120, En1BurstEnd = 25380, En1ReplyAt = 27970;

    // 05-cap24-1: burst [19760, 44920], reply sent at +50775.
    private static readonly (long At, long Start, string Text)[] Cap24FirstUser =
    [
        (611, 25600, " I'd"), (691, 25800, " like to"), (787, 26000, " hear"), (833, 26200, " it from"), (4849, 26600, " you"), (7077, 26800, " in"), (7146, 27000, " your"), (7146, 27200, " own"), (7248, 27800, " words"), (7386, 28600, ", briefly"), (7386, 28800, " and"), (7420, 29600, " precisely"), (7454, 30000, ". If"), (7488, 30200, " some of"), (7521, 30400, " this"), (7524, 30600, " is"), (7524, 30800, " not"), (7558, 31200, " covered in"), (7558, 31400, " your"), (7593, 32000, " material"), (7627, 32400, ", that is"), (7661, 32800, " fine"), (7730, 33400, ". Just"), (7730, 33600, " tell"), (7730, 33800, " me"), (7765, 34400, ", and I"), (7765, 34600, " will"), (7765, 34800, " follow"), (7765, 35000, " up"), (7799, 35200, " with the"), (7799, 35400, " right"), (7831, 35800, " people"), (7833, 36800, ". So"), (7867, 37200, ", keeping"), (7902, 37400, " all of"), (7902, 37600, " that"), (7902, 37800, " in"), (7969, 38400, " mind"), (7969, 38800, ", here is"), (7969, 39000, " what"), (7969, 39200, " I would"), (7969, 39400, " like"), (8004, 39600, " to"), (8004, 40000, " understand"), (8004, 40200, " about"), (8004, 40400, " the"), (8004, 40800, " programme"), (8039, 41000, " and"), (8039, 41200, " its"), (8039, 41400, " first"), (8073, 41800, " year"), (8073, 42400, ". What"), (8107, 42800, " did"), (8107, 43000, " the"), (8107, 43600, " programme"), (8142, 44200, " change"), (8142, 44400, " at the"), (8142, 44600, " Da"), (8176, 44800, " Nang"), (8176, 45600, " office in"), (8177, 45800, " its"), (8209, 46200, " first"), (8210, 46600, " year"), (8211, 47000, "? And"), (8211, 47200, " how"), (8243, 47600, " much"), (8244, 47800, " did the"), (8362, 48200, " Hanoi"), (8496, 48600, " expansion"), (8831, 49200, " cost"), (52429, 76400, " Yes")
    ];
    private static readonly (long At, long Start, string Text)[] Cap24FirstAssistant =
    [
        (8831, 49200, " In its"), (8958, 49400, " first"), (9040, 49600, " year,"), (28611, 50000, " the programme"), (28936, 50600, " moved"), (29154, 51000, " the Da"), (29336, 51200, " Nang"), (29464, 51600, " office"), (29727, 52000, " to"), (29820, 52200, " paperless"), (30129, 52800, " contracts,"), (30558, 53600, " and"), (30654, 53800, " the Hanoi"), (30859, 54200, " expansion"), (31185, 54800, " cost"), (31304, 55000, " "), (31424, 55200, "4."), (31519, 55400, "2"), (31902, 55800, " billion"), (32290, 56200, " dong.")
    ];
    private static readonly (long Start, long Stop)[] Cap24FirstVoiced = [(9416, 9916), (28595, 35695)];
    private const long Cap24FirstBurstStart = 19760, Cap24FirstBurstEnd = 44920, Cap24FirstReplyAt = 50775;

    // 06-cap24-2: burst [16000, 41160], reply sent at +47645.
    private static readonly (long At, long Start, string Text)[] Cap24SecondUser =
    [
        (418, 18800, " like to"), (492, 19000, " hear"), (571, 19200, " it"), (642, 19400, " from"), (717, 19600, " you"), (792, 19800, " in"), (868, 20000, " your"), (911, 20200, " own"), (1073, 20800, " words"), (1328, 21600, ", briefly"), (1370, 21800, " and"), (1531, 22400, " precisely"), (1756, 23000, ". If"), (1832, 23200, " some of"), (1906, 23400, " this"), (1982, 23600, " is"), (2025, 23800, " not"), (2195, 24200, " covered in"), (2235, 24400, " your"), (2413, 25000, " material"), (2565, 25400, ", that is"), (2725, 26000, " fine"), (2894, 26400, ". Just"), (2966, 26600, " tell"), (3015, 26800, " me"), (3236, 27400, ", and I"), (3312, 27600, " will"), (3387, 27800, " follow"), (3479, 28000, " up"), (3554, 28200, " with the"), (3603, 28400, " right"), (3717, 28800, " people"), (3997, 29800, ". So"), (4185, 30200, ", keeping"), (4260, 30400, " all of"), (4332, 30600, " that"), (4375, 30800, " in"), (4540, 31400, " mind"), (4720, 31800, ", here"), (4812, 32000, " is what"), (4887, 32200, " I would"), (4962, 32400, " like"), (5005, 32600, " to"), (5156, 33000, " understand"), (5232, 33200, " about"), (5277, 33400, " the"), (5429, 33800, " programme"), (5506, 34000, " and"), (5580, 34200, " its"), (5622, 34400, " first"), (5742, 34800, " year"), (5918, 35400, ". What"), (6068, 35800, " did"), (6142, 36000, " "), (6188, 36200, "the"), (6305, 36600, " programme"), (6517, 37200, " change"), (6591, 37400, " at the"), (6636, 37600, " Da"), (6754, 38000, " Nang"), (6965, 38600, " office in"), (7027, 38800, " its"), (7142, 39200, " first"), (7261, 39600, " year"), (7473, 40200, "? And"), (7548, 40400, " how"), (7638, 40600, " much"), (7685, 40800, " did the"), (7805, 41200, " Hanoi"), (7922, 41600, " expansion"), (8239, 42200, " cost"), (54721, 73200, " Yes")
    ];
    private static readonly (long At, long Start, string Text)[] Cap24SecondAssistant =
    [
        (8239, 42200, " It moved"), (8321, 42400, " the Da"), (8410, 42600, " Nang"), (25646, 43000, " office"), (25899, 43400, " to paper"), (27199, 43600, "less"), (28276, 44000, " contracts"), (28410, 44600, "."), (28443, 44800, " The"), (28589, 45000, " Hanoi"), (28632, 45400, " expansion"), (28733, 45800, " cost"), (28766, 46200, " "), (28800, 46400, "4."), (28802, 46600, "2"), (28834, 47000, " billion"), (28907, 47400, " dong"), (29000, 47600, ".")
    ];
    private static readonly (long Start, long Stop)[] Cap24SecondVoiced = [(8637, 9137), (25548, 25949), (28192, 30647)];
    private const long Cap24SecondBurstStart = 16000, Cap24SecondBurstEnd = 41160, Cap24SecondReplyAt = 47645;

    private static List<ProbeEvent> Run1Events()
    {
        var events = Run1Burst
            .Select(delta => new ProbeEvent(AskDone + delta.At, EventKind.User, delta.Text, delta.Start, delta.Start + 200))
            .ToList();
        events.Add(new ProbeEvent(AskDone + 2_300, EventKind.Assistant, " It did."));
        for (var at = 2_600L; at <= 3_000; at += 100)
        {
            events.Add(new ProbeEvent(AskDone + at, EventKind.Voiced, string.Empty, VoicedMs: 100));
        }

        events.Add(new ProbeEvent(AskDone + 2_900, EventKind.Assistant, " It"));
        events.Add(new ProbeEvent(AskDone + 7_739, EventKind.Assistant, " moved to paperless contracts."));
        for (var at = 7_739L; at <= 8_339; at += 100)
        {
            events.Add(new ProbeEvent(AskDone + at, EventKind.Voiced, string.Empty, VoicedMs: 100));
        }

        events.Add(new ProbeEvent(AskDone + 40_000 + 1_392, EventKind.User, " Yes", 39_600, 39_800));
        return Sorted(events);
    }

    // Live run 2 (English, vad, --trace): one answer with a 3.4 s delivery stall between " Nang" and " office".
    private static List<ProbeEvent> Run2Events()
    {
        (long At, long Start, string Text)[] user =
        [
            (465, 18000, "What did the"), (651, 18600, " program"), (839, 19000, " change at"), (893, 19200, " the"),
            (1047, 19600, " Da Nang"), (1238, 20000, " office"), (1291, 20200, " in its"), (1434, 20600, " first"),
            (1562, 21000, " year"), (1755, 21400, ", and"), (1839, 21600, " how"), (1929, 21800, " much"),
            (2014, 22000, " did"), (2062, 22200, " the"), (2198, 22600, " Hanoi"), (2485, 23200, " expansion"),
            (2835, 23800, " cost")
        ];
        (long At, long Start, string Text)[] assistant =
        [
            (2596, 23400, " It moved"), (2716, 23600, " the"), (2835, 23800, " Da"), (2959, 24000, " Nang"),
            (7123, 24200, " office"), (7540, 24600, " to"), (7673, 24800, " paperless"), (7892, 25200, " contracts,"),
            (8355, 26000, " and"), (8470, 26200, " the"), (8567, 26400, " Hanoi"), (8783, 26800, " expansion"),
            (8996, 27200, " cost"), (9234, 27600, " 4"), (9352, 27800, ".2"), (9781, 28200, " billion"), (9970, 28400, " dong.")
        ];
        var events = user.Select(d => new ProbeEvent(AskDone + d.At, EventKind.User, d.Text, d.Start, d.Start + 200))
            .Concat(assistant.Select(d => new ProbeEvent(AskDone + d.At, EventKind.Assistant, d.Text, d.Start, d.Start + 200)))
            .ToList();
        for (var at = 3_144L; at <= 3_743; at += 100)
        {
            events.Add(new ProbeEvent(AskDone + at, EventKind.Voiced, string.Empty, VoicedMs: 100));
        }

        events.Add(new ProbeEvent(AskDone + 3_743, EventKind.Voiced, string.Empty, VoicedMs: 100));
        for (var at = 7_216L; at <= 11_917; at += 100)
        {
            events.Add(new ProbeEvent(AskDone + at, EventKind.Voiced, string.Empty, VoicedMs: 100));
        }

        events.Add(new ProbeEvent(AskDone + 11_917, EventKind.Voiced, string.Empty, VoicedMs: 100));
        events.Add(new ProbeEvent(AskDone + 20_116, EventKind.User, "Yes", 38_800, 39_000));
        return Sorted(events);
    }

    private static ProbeRun Run2() =>
        new(ProbeDeck.English, 5_000, 9_000, AskDone, AskDone, AskDone + 34, 17_040, 24_440, null,
            AskDone + 3_144, AskDone + 11_917, AskDone + 18_811, 35_860, 34,
            new AskRecorderStats(17_600, 6_400, 5_600, 16_800, new AskRmsBands(0, 0, 0, 0, 0), 1_000, false),
            false, false, new HashSet<string>());

    private static List<ProbeEvent> Sorted(List<ProbeEvent> events) => events.OrderBy(e => e.At).ToList();

    private static ProbeRun Run(long answerStart, long answerEnd, bool trace = false, string[]? endTypes = null) =>
        new(ProbeDeck.English, 5_000, 9_000, AskDone, AskDone + 1, AskDone + 38, 18_780, 26_180, null,
            AskDone + answerStart, AskDone + answerEnd, AskDone + 40_000, 36_880, 38,
            new AskRecorderStats(17_600, 6_400, 5_600, 16_800, new AskRmsBands(0, 0, 0, 0, 0), 1_000, false),
            trace, endTypes is { Length: > 0 }, (endTypes ?? []).ToHashSet());
}
