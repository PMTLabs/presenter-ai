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
        text.Should().Contain("PASS (iv) every user delta before the reply is in the burst range, part 1 before part 2: 18 deltas, all in the burst range; part-1 keyword before part-2 keyword");
        text.Should().Contain("[answer]: \"It did. It\"")
            .And.Contain("+7739..+8339 ms after Ask done, 0.7 s voiced: \"moved to paperless contracts.\"");
        text.Should().Contain("FAIL (vi) no second unsolicited response within 15 s: a new response started 4739 ms after the answer ended");
        text.Should().Contain("FAIL (v) one answer with fact A and fact B: fact A missing; fact B missing");
    }

    [Fact]
    public async Task A_user_delta_outside_the_burst_range_before_the_reply_fails_iv()
    {
        var events = Run1Events();
        events.Add(new ProbeEvent(AskDone + 2_500, EventKind.User, " cost", 27_000, 27_200));

        var output = new StringWriter();
        await ReportAsync(Run(answerStart: 2_600, answerEnd: 3_000), Sorted(events), output);

        output.ToString().Should().Contain("FAIL (iv) every user delta before the reply is in the burst range, part 1 before part 2: 1 user deltas outside the burst range before the reply (start_ms 27000)");
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

        output.ToString().Should().Contain("FAIL (iv) every user delta before the reply is in the burst range, part 1 before part 2: part-2 keyword missing");
    }

    // Live runs en-3, en-4 and raw: the model began answering while the burst transcript was still arriving (and, raw,
    // answered part 1 before part 2 was transcribed). Every user delta is in the burst range, so (iv) passes.
    // Live runs en-3, en-4 and raw: the model began answering while the burst transcript was still arriving (and, raw,
    // answered part 1 before part 2 was transcribed). Every user delta is in the burst range, so (iv) passes.
    [Fact]
    public Task Live_replay_en3_passes_iv_by_timestamps() => AssertReplayPassesIvAsync(
        "en-3",
        [(421, 18000, " What did"), (548, 18200, " the"), (652, 18800, " programme"), (835, 19200, " change at"), (922, 19400, " the"),
             (997, 19600, " Da"), (1056, 19800, " Nang"), (1231, 20200, " office in"), (1276, 20400, " its"), (1528, 20800, " first"),
             (1744, 21200, " year"), (1906, 21600, " And"), (1988, 21800, " how"), (2064, 22000, " much"), (2145, 22200, " did"),
             (2197, 22400, " the"), (2324, 22800, " Hanoi"), (2596, 23400, " expansion"), (2831, 23800, " cost")],
        [(1529, 20800, " It moved"), (1992, null, ""), (2092, null, ""), (2719, 23600, " the Da"), (2833, 23800, " Nang"), (2935, 24000, " office")],
        17180, 24580, 26201, 46000);

    [Fact]
    public Task Live_replay_en4_passes_iv_by_timestamps() => AssertReplayPassesIvAsync(
        "en-4",
        [(368, 17800, "What"), (453, 18000, " did"), (501, 18200, " the"), (640, 18600, " programme"), (848, 19000, " change"),
             (940, 19200, " at the"), (1034, 19400, " Da"), (1072, 19600, " Nang"), (1254, 20000, " office"), (1348, 20200, " in"),
             (1388, 20400, " its"), (1650, 20800, " first"), (1762, 21000, " year"), (2008, 21600, " and how"), (2179, 22000, " much"),
             (2208, 22200, " did the"), (2374, 22600, " Hanoi"), (2522, 23200, " expansion"), (2896, 23800, " cost")],
        [(1650, 20800, " It moved"), (1762, 21000, " the"), (2449, null, ""), (2786, 23600, " It moved"), (2896, 23800, " the")],
        17040, 24440, 27587, 47400);

    [Fact]
    public Task Live_replay_raw_passes_iv_by_timestamps() => AssertReplayPassesIvAsync(
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

        output.ToString().Should().Contain("PASS (iv) every user delta before the reply is in the burst range, part 1 before part 2", name)
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
            .And.Contain("FAIL (iv) every user delta before the reply is in the burst range, part 1 before part 2: part-1 keyword missing; part-2 keyword missing");
    }

    [Fact]
    public async Task A_whole_answer_with_both_facts_passes_and_the_trace_lists_upstream_events()
    {
        var events = Run1Events().Where(e => e.At < AskDone + 7_000).ToList();
        events.Add(new ProbeEvent(AskDone + 2_700, EventKind.Assistant, " It moved to paperless contracts, and Hanoi cost 4.2 billion dong. Shall I carry on?"));
        events.Add(new ProbeEvent(AskDone + 3_050, EventKind.Raw, "response_id=\"r1\"", Type: "session.output_audio.done"));
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
