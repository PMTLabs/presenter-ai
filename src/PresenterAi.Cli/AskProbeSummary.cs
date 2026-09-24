using System.Globalization;
using System.Text.RegularExpressions;

namespace PresenterAi.Cli;

/// <summary>
/// Plan 011 T1 suite: reads the probe logs of one suite run (<c>NN-&lt;kind&gt;-&lt;n&gt;.log</c>, kind = en, vi, cap16,
/// cap24, cap28, cap40, raw or interrupt), prints a table and the T1 verdict. Rules (plan §6 T1 with the owner's
/// 2026-09-24 decisions: timestamp-based (iv), a 25 s kept-speech cap sent unpaced):
/// <list type="bullet">
/// <item>en ×3, vi ×1 and cap24 ×3 each PASS (i)–(vii) and the reply check (turn-taking, owner decision 2026-09-24) and
/// are not TRUNCATED; provenance is a diagnostic column only;</item>
/// <item>cap40 (over the cap) is TRUNCATED or FAILs, which confirms the limit;</item>
/// <item>cap16, cap28, raw and interrupt are evidence only, but must have completed (usage printed).</item>
/// </list>
/// It also derives <c>AnswerStartBudgetMs</c> = max(15 s, 1.5 × the longest Ask-done→first-answer-audio of the
/// required runs), rounded up to 5 s.
/// </summary>
internal static partial class AskProbeSummary
{
    private static readonly string[] Criteria = ["i", "ii", "iii", "iv", "v", "vi", "vii", "reply"];

    internal static readonly IReadOnlyDictionary<string, int> RequiredPasses = new Dictionary<string, int>
    {
        ["en"] = 3,
        ["vi"] = 1,
        ["cap24"] = 3
    };

    internal sealed record RunResult(
        string Name,
        string Kind,
        IReadOnlyDictionary<string, bool> Criteria,
        bool Truncated,
        long? LatencyMs,
        double? UsageSeconds,
        bool? Passed,
        bool? Provenance,
        long? WireLagMs = null,
        long? OverrunMs = null);

    internal sealed record Verdict(bool Passed, IReadOnlyList<string> Reasons, long? AnswerStartBudgetMs, double TotalUsageSeconds);

    public static async Task<int> RunAsync(AskProbeSummaryArguments arguments, TextWriter output, TextWriter error)
    {
        if (!Directory.Exists(arguments.LogDirectory))
        {
            await error.WriteLineAsync($"ask-probe-summary: no such directory: {arguments.LogDirectory}").ConfigureAwait(false);
            return 2;
        }

        var runs = new List<RunResult>();
        foreach (var path in Directory.GetFiles(arguments.LogDirectory, "*.log").Order(StringComparer.Ordinal))
        {
            runs.Add(Parse(Path.GetFileNameWithoutExtension(path), await File.ReadAllTextAsync(path).ConfigureAwait(false)));
        }

        var verdict = Judge(runs);
        await output.WriteLineAsync(Table(runs, verdict)).ConfigureAwait(false);
        return verdict.Passed ? 0 : 1;
    }

    internal static RunResult Parse(string name, string log)
    {
        var kind = KindPattern().Match(name) is { Success: true } k ? k.Groups["kind"].Value : "unknown";
        var criteria = new Dictionary<string, bool>();
        foreach (Match match in CriterionPattern().Matches(log))
        {
            criteria[match.Groups["id"].Value] = match.Groups["verdict"].Value == "PASS";
        }

        var latency = LatencyPattern().Match(log) is { Success: true } l ? long.Parse(l.Groups["ms"].Value, CultureInfo.InvariantCulture) : (long?)null;
        var usages = UsagePattern().Matches(log);
        var usage = usages.Count > 0 && double.TryParse(usages[^1].Groups["s"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var u) ? u : (double?)null;
        var result = ResultPattern().Match(log) is { Success: true } r ? r.Groups["verdict"].Value == "PASS" : (bool?)null;
        var provenance = ProvenancePattern().Match(log) is { Success: true } p ? p.Groups["verdict"].Value == "CONFIRMED" : (bool?)null;
        var truncated = log.Contains("truncation: TRUNCATED", StringComparison.Ordinal);
        var wire = WirePattern().Match(log) is { Success: true } w ? long.Parse(w.Groups["ms"].Value, CultureInfo.InvariantCulture) : (long?)null;
        var overrun = OverrunPattern().Match(log) is { Success: true } o ? long.Parse(o.Groups["ms"].Value, CultureInfo.InvariantCulture) : (long?)null;
        return new RunResult(name, kind, criteria, truncated, latency, usage, result, provenance, wire, overrun);
    }

    internal static Verdict Judge(IReadOnlyList<RunResult> runs)
    {
        var reasons = new List<string>();
        foreach (var (kind, count) in RequiredPasses)
        {
            var ofKind = runs.Where(run => run.Kind == kind).ToList();
            if (ofKind.Count < count)
            {
                reasons.Add($"{kind}: {ofKind.Count} of {count} runs present");
            }

            foreach (var run in ofKind)
            {
                if (run.Passed != true || Criteria.Any(id => !run.Criteria.GetValueOrDefault(id)))
                {
                    reasons.Add($"{run.Name}: failed {string.Join(", ", Criteria.Where(id => !run.Criteria.GetValueOrDefault(id)).Select(id => $"({id})").DefaultIfEmpty("(result)"))}");
                }

                if (run.Truncated)
                {
                    reasons.Add($"{run.Name}: TRUNCATED");
                }

            }
        }

        var overCap = runs.Where(run => run.Kind == "cap40").ToList();
        if (overCap.Count == 0)
        {
            reasons.Add("cap40: the over-cap control is missing");
        }

        foreach (var run in overCap.Where(run => !run.Truncated && run.Passed == true))
        {
            reasons.Add($"{run.Name}: passed untruncated, so the 25 s cap is not confirmed");
        }

        foreach (var run in runs.Where(run => run.Kind is "cap16" or "cap28" or "raw" or "interrupt" && run.UsageSeconds is null))
        {
            reasons.Add($"{run.Name}: did not complete (no usage)");
        }

        foreach (var kind in new[] { "raw", "interrupt" }.Where(kind => runs.All(run => run.Kind != kind)))
        {
            reasons.Add($"{kind}: the evidence run is missing");
        }

        var latencies = runs.Where(run => RequiredPasses.ContainsKey(run.Kind) && run.LatencyMs is not null).Select(run => run.LatencyMs!.Value).ToList();
        long? budget = latencies.Count == 0 ? null : (long)(Math.Ceiling(Math.Max(15_000, 1.5 * latencies.Max()) / 5_000) * 5_000);
        return new Verdict(reasons.Count == 0, reasons, budget, runs.Sum(run => run.UsageSeconds ?? 0));
    }

    internal static string Table(IReadOnlyList<RunResult> runs, Verdict verdict)
    {
        var lines = new List<string>
        {
            $"{"run",-18} {"i",-4} {"ii",-4} {"iii",-4} {"iv",-4} {"v",-4} {"vi",-4} {"vii",-4} {"rply",-4} {"trunc",-6} {"prov",-5} {"latency",9} {"wire lag",9} {"overrun",8} {"usage s",8}  result",
            new string('-', 121)
        };
        foreach (var run in runs)
        {
            string Cell(string id) => run.Criteria.TryGetValue(id, out var pass) ? (pass ? "PASS" : "FAIL") : "-";
            lines.Add(
                $"{run.Name,-18} {Cell("i"),-4} {Cell("ii"),-4} {Cell("iii"),-4} {Cell("iv"),-4} {Cell("v"),-4} {Cell("vi"),-4} {Cell("vii"),-4} {Cell("reply"),-4} " +
                $"{(run.Truncated ? "YES" : "no"),-6} {(run.Provenance is null ? "-" : run.Provenance.Value ? "ok" : "NO"),-5} " +
                $"{(run.LatencyMs is { } ms ? $"{ms} ms" : "-"),9} {(run.WireLagMs is { } wl ? $"{wl} ms" : "-"),9} " +
                $"{(run.OverrunMs is { } ov ? $"{ov:+#;-#;0}" : "-"),8} {(run.UsageSeconds is { } s ? s.ToString("0.0", CultureInfo.InvariantCulture) : "-"),8}  " +
                $"{(run.Passed is null ? "-" : run.Passed.Value ? "PASS" : "FAIL")}");
        }

        lines.Add(new string('-', 121));
        lines.Add($"total usage: {verdict.TotalUsageSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s over {runs.Count} runs");
        lines.Add($"AnswerStartBudgetMs: {(verdict.AnswerStartBudgetMs is { } b ? b.ToString(CultureInfo.InvariantCulture) : "n/a (no answer latency)")}");
        lines.Add($"T1 verdict: {(verdict.Passed ? "PASS" : "FAIL")}{(verdict.Reasons.Count == 0 ? string.Empty : " — " + string.Join("; ", verdict.Reasons))}");
        return string.Join(Environment.NewLine, lines);
    }

    [GeneratedRegex(@"^\d+-(?<kind>[a-z]+\d*)-\d+$")]
    private static partial Regex KindPattern();

    [GeneratedRegex(@"^(?<verdict>PASS|FAIL) \((?<id>i|ii|iii|iv|v|vi|vii|reply)\)", RegexOptions.Multiline)]
    private static partial Regex CriterionPattern();

    [GeneratedRegex(@"latency: Ask done -> first answer audio (?<ms>\d+) ms")]
    private static partial Regex LatencyPattern();

    [GeneratedRegex(@"on the wire after (?<ms>\d+) ms")]
    private static partial Regex WirePattern();

    [GeneratedRegex(@"^clock: overrun \(max user start_ms before the reply - end mark\) (?<ms>[+-]?\d+) ms", RegexOptions.Multiline)]
    private static partial Regex OverrunPattern();

    [GeneratedRegex(@"^usage\.seconds=(?<s>[0-9.]+)", RegexOptions.Multiline)]
    private static partial Regex UsagePattern();

    [GeneratedRegex(@"^result: (?<verdict>PASS|FAIL)", RegexOptions.Multiline)]
    private static partial Regex ResultPattern();

    [GeneratedRegex(@"^provenance check(?: \(diagnostic, not scored\))?: (?<verdict>CONFIRMED|NOT CONFIRMED)", RegexOptions.Multiline)]
    private static partial Regex ProvenancePattern();
}

internal sealed record AskProbeSummaryArguments(string LogDirectory) : CliArguments;
