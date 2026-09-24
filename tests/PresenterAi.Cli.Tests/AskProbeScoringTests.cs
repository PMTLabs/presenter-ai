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
        text.Should().Contain("PASS (iv) exactly one user turn, part 1 before part 2: one turn; part-1 keyword before part-2 keyword; nothing between them");
        text.Should().Contain("[answer]: \"It did. It\"")
            .And.Contain("+7739..+8339 ms after Ask done, 0.7 s voiced: \"moved to paperless contracts.\"");
        text.Should().Contain("FAIL (vi) no second unsolicited response within 15 s: voiced audio 4739 ms after the answer ended");
        text.Should().Contain("FAIL (v) one answer with fact A and fact B: fact A missing; fact B missing");
    }

    [Fact]
    public async Task A_user_delta_outside_the_burst_after_assistant_output_is_a_second_turn()
    {
        var events = Run1Events();
        events.Add(new ProbeEvent(AskDone + 2_500, EventKind.User, " cost", 27_000, 27_200));

        var output = new StringWriter();
        await ReportAsync(Run(answerStart: 2_600, answerEnd: 3_000), Sorted(events), output);

        output.ToString().Should().Contain("FAIL (iv) exactly one user turn, part 1 before part 2: 2 user turns (burst turn present, 1 other)");
    }

    [Fact]
    public async Task Assistant_audio_between_the_two_halves_fails_iv()
    {
        var events = Run1Events();
        events.Add(new ProbeEvent(AskDone + 1_000, EventKind.Voiced, string.Empty, VoicedMs: 100));

        var output = new StringWriter();
        await ReportAsync(Run(answerStart: 2_600, answerEnd: 3_000), Sorted(events), output);

        output.ToString().Should().Contain("FAIL (iv)").And.Contain("1 assistant/delegation events arrived between the part-1 and part-2 keywords");
    }

    [Fact]
    public async Task Missing_keyword_is_reported_as_the_reason()
    {
        var events = Run1Events().Where(e => e.Text != " Hanoi").ToList();

        var output = new StringWriter();
        await ReportAsync(Run(answerStart: 2_600, answerEnd: 3_000), events, output);

        output.ToString().Should().Contain("FAIL (iv) exactly one user turn, part 1 before part 2: part-2 keyword missing");
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

    private static List<ProbeEvent> Sorted(List<ProbeEvent> events) => events.OrderBy(e => e.At).ToList();

    private static ProbeRun Run(long answerStart, long answerEnd, bool trace = false, string[]? endTypes = null) =>
        new(ProbeDeck.English, 5_000, 9_000, AskDone, AskDone + 1, AskDone + 38, 18_780, 26_180, null,
            AskDone + answerStart, AskDone + answerEnd, AskDone + 40_000, 36_880, 38,
            new AskRecorderStats(17_600, 6_400, 5_600, 16_800, new AskRmsBands(0, 0, 0, 0, 0), 1_000, false),
            trace, endTypes is { Length: > 0 }, (endTypes ?? []).ToHashSet());
}
