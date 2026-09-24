using System.Buffers.Binary;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using PresenterAi.Cli;
using PresenterAi.Infrastructure.Live;
using Xunit;

namespace PresenterAi.Cli.Tests;

/// <summary>Plan 011 T1 suite: the summary parser and verdict, the TTS request/response and audio helpers, compose.</summary>
public sealed class AskProbeSuiteTests
{
    [Fact]
    public void Summary_passes_a_complete_matrix_and_derives_the_answer_budget()
    {
        var runs = CompleteMatrix();

        var verdict = AskProbeSummary.Judge(runs);

        verdict.Passed.Should().BeTrue(string.Join("; ", verdict.Reasons));
        verdict.AnswerStartBudgetMs.Should().Be(15_000, "1.5 x 3.3 s is under the 15 s floor");
        verdict.TotalUsageSeconds.Should().Be(11 * 50);
        AskProbeSummary.Table(runs, verdict).Should().Contain("T1 verdict: PASS").And.Contain("09-cap40-1").And.Contain("YES");
    }

    [Fact]
    public void Summary_fails_when_a_required_run_fails_is_truncated_or_the_over_cap_control_passes()
    {
        var runs = CompleteMatrix();
        runs[1] = AskProbeSummary.Parse("02-en-2", Log(pass: false, failing: "v"));
        runs[4] = AskProbeSummary.Parse("05-cap24-1", Log(pass: true, truncated: true));
        runs[8] = AskProbeSummary.Parse("09-cap40-1", Log(pass: true));
        runs.RemoveAt(3);

        var verdict = AskProbeSummary.Judge(runs);

        verdict.Passed.Should().BeFalse();
        verdict.Reasons.Should().Contain("02-en-2: failed (v)")
            .And.Contain("05-cap24-1: TRUNCATED")
            .And.Contain("09-cap40-1: passed untruncated, so the 25 s cap is not confirmed")
            .And.Contain("vi: 0 of 1 runs present");
    }

    [Fact]
    public void Summary_parser_reads_criteria_truncation_latency_usage_and_provenance()
    {
        var run = AskProbeSummary.Parse("09-cap40-1", Log(pass: false, failing: "iv", truncated: true, latencyMs: 32_893, usage: 59.1, provenance: false));

        run.Kind.Should().Be("cap40");
        run.Criteria.Should().HaveCount(6);
        run.Criteria["iv"].Should().BeFalse();
        run.Criteria["v"].Should().BeTrue();
        run.Truncated.Should().BeTrue();
        run.LatencyMs.Should().Be(32_893);
        run.UsageSeconds.Should().Be(59.1);
        run.Provenance.Should().BeFalse();
        run.Passed.Should().BeFalse();

        var withClock = AskProbeSummary.Parse("05-cap24-1",
            "burst: 126 chunks (25.2 s of audio) at pace 0 (unpaced burst); send duration 0 ms to queue, on the wire after 7662 ms; input clock [19760, 44920] ms\n" +
            "clock: overrun (max user start_ms before the reply - end mark) +4280 ms; upstream-clock offset estimate (...) +5480 ms; wire lag 7662 ms\n" +
            "reply: on the wire after 900 ms\n");
        withClock.WireLagMs.Should().Be(7_662);
        withClock.OverrunMs.Should().Be(4_280);
        AskProbeSummary.Table([withClock], AskProbeSummary.Judge([withClock])).Should().Contain("7662 ms").And.Contain("+4280");
    }

    [Fact]
    public async Task Summary_command_exits_0_only_for_a_passing_log_directory()
    {
        var directory = Directory.CreateTempSubdirectory("ask-probe-suite-");
        try
        {
            foreach (var (name, log) in CompleteMatrixLogs())
            {
                await File.WriteAllTextAsync(Path.Combine(directory.FullName, name + ".log"), log);
            }

            var output = new StringWriter();
            var exit = await Program.RunAsync(["ask-probe-summary", directory.FullName], new ConfigurationBuilder().Build(), output, new StringWriter(), CancellationToken.None);
            exit.Should().Be(0, output.ToString());

            File.Delete(Path.Combine(directory.FullName, "04-vi-1.log"));
            exit = await Program.RunAsync(["ask-probe-summary", directory.FullName], new ConfigurationBuilder().Build(), new StringWriter(), new StringWriter(), CancellationToken.None);
            exit.Should().Be(1);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Tts_requires_one_text_source_and_an_output()
    {
        (string[] Args, string Message)[] cases =
        [
            (["tts", "--text", "hi"], "tts requires --out <wav>"),
            (["tts", "--out", "a.wav"], "tts requires exactly one of --text <t> or --text-file <f>"),
            (["tts", "--out", "a.wav", "--text", "hi", "--text-file", "t.txt"], "tts requires exactly one of --text <t> or --text-file <f>"),
            (["tts", "--out", "a.wav", "--text", "hi", "--provider", "google"], "tts requires --provider azure|openai"),
            (["tts", "--out", "a.wav", "--text", "hi", "--speed", "2"], "Unknown tts option: --speed")
        ];
        foreach (var (args, message) in cases)
        {
            var error = new StringWriter();
            (await Program.RunAsync(args, new ConfigurationBuilder().Build(), new StringWriter(), error, CancellationToken.None)).Should().Be(2);
            error.ToString().Should().Contain(message);
        }

        CliParser.Parse(["tts", "--out", "a.wav", "--text", "hi"], new StringWriter())
            .Should().Be(new TtsArguments("azure", "gpt-audio-1.5", "marin", "hi", null, "a.wav"));
    }

    [Theory]
    [InlineData("https://example-res.openai.azure.com", "https://example-res.openai.azure.com/openai/v1/chat/completions")]
    [InlineData("https://api.openai.com", "https://api.openai.com/v1/chat/completions")]
    public void Tts_posts_to_the_routes_chat_completions_url(string endpoint, string expected) =>
        TtsCommand.ChatCompletionsUrl(LiveUrlResolver.Resolve(endpoint)).ToString().Should().Be(expected);

    [Fact]
    public void Tts_request_asks_for_verbatim_text_and_wav_audio_and_the_response_yields_audio_and_transcript()
    {
        var body = TtsCommand.RequestBody("gpt-audio-1.5", "marin", "Yes.");
        body["modalities"]!.AsArray().Select(node => node!.GetValue<string>()).Should().Equal("text", "audio");
        body["audio"]!["voice"]!.GetValue<string>().Should().Be("marin");
        body["audio"]!["format"]!.GetValue<string>().Should().Be("wav");
        body["messages"]![0]!["content"]!.GetValue<string>().Should().Contain("exactly as written");
        body["messages"]![1]!["content"]!.GetValue<string>().Should().Be("Yes.");

        var wav = PcmWav.Write24kMono(new byte[480]);
        var response = JsonSerializer.Serialize(new { choices = new[] { new { message = new { audio = new { data = Convert.ToBase64String(wav), transcript = "Yes." } } } } });
        TtsCommand.TryParseAudio(response, out var audio, out var transcript).Should().BeTrue();
        audio.Should().Equal(wav);
        transcript.Should().Be("Yes.");
        TtsCommand.TryParseAudio("""{"choices":[{"message":{"content":"no audio"}}]}""", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Audio_is_downmixed_and_resampled_to_24_khz_mono()
    {
        // 48 kHz stereo: left 1000, right 3000 -> mono 2000, half the frames.
        var stereo = new byte[4 * 4_800];
        for (var frame = 0; frame < 4_800; frame++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(stereo.AsSpan(frame * 4), 1_000);
            BinaryPrimitives.WriteInt16LittleEndian(stereo.AsSpan(frame * 4 + 2), 3_000);
        }

        var mono = PcmWav.To24kMono(stereo, 48_000, 2);
        mono.Length.Should().Be(2 * 2_400);
        BinaryPrimitives.ReadInt16LittleEndian(mono.AsSpan(1_000)).Should().Be(2_000);

        // 16 kHz ramp -> 24 kHz: 1.5x the frames, linear in between.
        var ramp = new byte[2 * 1_600];
        for (var index = 0; index < 1_600; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(ramp.AsSpan(index * 2), (short)(index * 10));
        }

        var up = PcmWav.To24kMono(ramp, 16_000, 1);
        up.Length.Should().Be(2 * 2_400);
        BinaryPrimitives.ReadInt16LittleEndian(up.AsSpan(2 * 3)).Should().Be(20, "output frame 3 sits at input frame 2");
        BinaryPrimitives.ReadInt16LittleEndian(up.AsSpan(2 * 1)).Should().Be(7, "output frame 1 sits at input frame 0.667");

        var same = new byte[] { 1, 2, 3, 4 };
        PcmWav.To24kMono(same, 24_000, 1).Should().Equal(same);

        var file = PcmWav.Write24kMono(up);
        PcmWav.TryReadPcm16Mono24k(file, out var roundTrip).Should().BeNull();
        roundTrip.Should().Equal(up);
    }

    [Fact]
    public void Compose_picks_the_preamble_tail_whose_kept_length_is_nearest_the_target()
    {
        // Preamble: 20 words of 600 ms voiced, each followed by 300 ms of silence (18 s).
        var word = Level(600, 2_000);
        var pause = new byte[300 * 48];
        var preamble = Enumerable.Range(0, 20).SelectMany(_ => word.Concat(pause)).ToArray();
        var question = Level(3_000, 2_000);
        var part2 = Level(2_000, 2_000);

        var composition = ComposeAskWavCommand.Compose(preamble, question, part2, 10, 12_000);

        composition.KeptMs.Should().BeCloseTo(12_000, 500);
        ComposeAskWavCommand.KeptMs(composition.Part1, part2, 10).Should().Be(composition.KeptMs);
        composition.Part1.AsSpan(composition.Part1.Length - question.Length).SequenceEqual(question).Should().BeTrue("the question stays at the end");
    }

    private static byte[] Level(int ms, short level)
    {
        var bytes = new byte[ms * 48];
        for (var index = 0; index < bytes.Length / 2; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * 2), (short)(index % 2 == 0 ? level : -level));
        }

        return bytes;
    }

    private static List<AskProbeSummary.RunResult> CompleteMatrix() =>
        CompleteMatrixLogs().Select(pair => AskProbeSummary.Parse(pair.Name, pair.Log)).ToList();

    private static IEnumerable<(string Name, string Log)> CompleteMatrixLogs()
    {
        foreach (var name in new[] { "01-en-1", "02-en-2", "03-en-3", "04-vi-1", "05-cap24-1", "06-cap24-2", "07-cap24-3", "08-cap28-1" })
        {
            yield return (name, Log(pass: true));
        }

        yield return ("09-cap40-1", Log(pass: false, failing: "iv", truncated: true));
        yield return ("10-raw-1", Log(pass: true));
        yield return ("11-interrupt-1", "interrupt (narration): muted 1500 ms after the first narration audio\nusage.seconds=50\n");
    }

    private static string Log(bool pass, string? failing = null, bool truncated = false, long latencyMs = 3_300, double usage = 50, bool provenance = true)
    {
        var lines = new List<string>
        {
            "ask-probe: provider=azure route=primary model=gpt-live-1",
            $"latency: Ask done -> first answer audio {latencyMs} ms; last chunk queued -> {latencyMs} ms",
            truncated ? "truncation: TRUNCATED (the reply starts at 55600 on the input clock)" : "truncation: none detected"
        };
        foreach (var id in new[] { "i", "ii", "iii", "iv", "v", "vi" })
        {
            lines.Add($"{(id == failing ? "FAIL" : "PASS")} ({id}) criterion text");
        }

        lines.Add($"provenance check: {(provenance ? "CONFIRMED" : "NOT CONFIRMED")} (burst deltas in range)");
        lines.Add($"result: {(pass ? "PASS" : "FAIL")}{(truncated ? " TRUNCATED" : string.Empty)} (variant run)");
        lines.Add("closed: reason=close_requested");
        lines.Add($"usage.seconds={usage.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        return string.Join('\n', lines);
    }
}
