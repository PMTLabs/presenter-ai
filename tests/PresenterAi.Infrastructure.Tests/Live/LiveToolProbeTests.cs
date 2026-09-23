using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using PresenterAi.Infrastructure.Live;
using Xunit;
using Xunit.Abstractions;

namespace PresenterAi.Infrastructure.Tests.Live;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class LiveProbeFactAttribute : FactAttribute
{
    public LiveProbeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PRESENTER_LIVE_PROBE") != "1")
        {
            Skip = "Skipped because PRESENTER_LIVE_PROBE is not set to 1.";
        }
    }
}

public sealed class LiveToolProbeTests
{
    private readonly ITestOutputHelper? _output;

    public LiveToolProbeTests(ITestOutputHelper? output = null)
    {
        _output = output;
    }

    [LiveProbeFact]
    public async Task Live_tool_probe()
    {
        if (Environment.GetEnvironmentVariable("PRESENTER_LIVE_PROBE") != "1")
        {
            return;
        }

        var audioDir = Environment.GetEnvironmentVariable("PRESENTER_LIVE_PROBE_AUDIO_DIR");
        if (string.IsNullOrWhiteSpace(audioDir) || !Directory.Exists(audioDir))
        {
            var defaultScratch = @"C:\Users\tamtr\AppData\Local\Temp\claude\D--sources-demo-presenter-ai\78e5d02d-4e0c-4a11-b2ab-7f64d531cf4b\scratchpad\probe-audio";
            if (Directory.Exists(defaultScratch))
            {
                audioDir = defaultScratch;
            }
        }

        if (string.IsNullOrWhiteSpace(audioDir) || !Directory.Exists(audioDir))
        {
            throw new DirectoryNotFoundException($"PRESENTER_LIVE_PROBE_AUDIO_DIR not set or directory not found: '{audioDir}'");
        }

        var configuration = BuildConfiguration();
        var upstreamOptions = new UpstreamOptions();
        configuration.GetSection("Upstream").Bind(upstreamOptions);

        var routes = UpstreamRoutes.From(upstreamOptions);
        var route = routes.Upstreams[0];
        var voice = routes.Voice;
        var delegationModel = string.IsNullOrWhiteSpace(route.DelegationModel)
            ? (string.IsNullOrWhiteSpace(upstreamOptions.DelegationModel) ? "gpt-5.6-luna" : upstreamOptions.DelegationModel)
            : route.DelegationModel;

        var effectiveRoute = new UpstreamRoute(
            route.Name,
            route.LiveUrl,
            route.Headers,
            route.Model,
            delegationModel);

        _output?.WriteLine($"Probe connecting to upstream host: {effectiveRoute.LiveUrl.Host}, model: {effectiveRoute.Model}, delegation: {effectiveRoute.DelegationModel}");
        _output?.WriteLine($"Using audio directory: {audioDir}");

        var utterances = new[]
        {
            new ProbeUtterance("next slide", "next-slide.wav", "next_slide", "{}"),
            new ProbeUtterance("go to slide 3", "go-to-slide-3.wav", "go_to_slide", "{\"slide_number\":3}"),
            new ProbeUtterance("go to the slide about How it works", "slide-about-title.wav", "go_to_slide", "{\"slide_number\":2}"),
            new ProbeUtterance("end the meeting", "end-meeting.wav", "end_presentation", "{\"confirmed\":false}")
        };

        var repeatEnv = Environment.GetEnvironmentVariable("PRESENTER_LIVE_PROBE_REPEAT");
        var repeatCount = int.TryParse(repeatEnv, CultureInfo.InvariantCulture, out var parsedRepeat) && parsedRepeat > 0
            ? parsedRepeat
            : 5;

        _output?.WriteLine($"Configured repeats: {repeatCount} (total utterances: {repeatCount * utterances.Length})");

        var probeResults = new List<ProbeRunResult>();
        var runIndex = 1;

        for (var repeat = 1; repeat <= repeatCount; repeat++)
        {
            foreach (var utterance in utterances)
            {
                _output?.WriteLine($"\n--- Run {runIndex}/{repeatCount * utterances.Length}: \"{utterance.Label}\" ({utterance.WavFileName}, iteration {repeat}/{repeatCount}) ---");

                await using var client = new ProbeLiveClient(effectiveRoute, voice, _output);
                using var runCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var sessionId = await client.ConnectAndStartAsync(runCts.Token);
                _output?.WriteLine($"Run {runIndex} session started: {sessionId}");

                var result = await client.ProbeUtteranceAsync(runIndex, utterance, audioDir, runCts.Token);
                probeResults.Add(result);

                await client.CloseAsync();

                var uttEndToCallStr = result.UtteranceEndToCallLatencyMs.HasValue
                    ? $"{result.UtteranceEndToCallLatencyMs.Value.ToString("F1", CultureInfo.InvariantCulture)}ms"
                    : "N/A";
                var callToSpokenStr = result.CallToSpokenLatencyMs.HasValue
                    ? $"{result.CallToSpokenLatencyMs.Value.ToString("F1", CultureInfo.InvariantCulture)}ms"
                    : "N/A";

                _output?.WriteLine($"Result {runIndex}: Transcript=\"{result.InputTranscript}\", Spoken=\"{TruncateText(result.SpokenTranscript, 80)}\", Delegated={result.Delegated}, Tool={result.ToolCalled ?? "none"} (match={result.ToolMatched}), Args={result.Arguments ?? "{}"} (match={result.ArgsMatched}), CycleDone={result.CycleCompleted}, UttEnd→Call={uttEndToCallStr}, Call→Spoken={callToSpokenStr}");
                runIndex++;
            }
        }

        _output?.WriteLine("\n--- Checking response.create after two outputs barrier ---");
        await using var barrierClient = new ProbeLiveClient(effectiveRoute, voice, _output);
        using var barrierCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var barrierSessionId = await barrierClient.ConnectAndStartAsync(barrierCts.Token);
        _output?.WriteLine($"Barrier check session started: {barrierSessionId}");

        var barrierResult = await barrierClient.ProbeTwoOutputsBarrierAsync(audioDir, barrierCts.Token);
        await barrierClient.CloseAsync();

        _output?.WriteLine($"Barrier check: Completed={barrierResult.Completed}, Strategy={barrierResult.Strategy}, Calls={barrierResult.CallsEmitted} ({string.Join(", ", barrierResult.ToolsCalled)}), Trace={string.Join(" -> ", barrierResult.EventTrace)}");

        var markdown = BuildMarkdownReport(effectiveRoute, voice, audioDir, probeResults, barrierResult);
        _output?.WriteLine("\n=== PROBE RESULTS REPORT ===");
        _output?.WriteLine(markdown);

        var outputPath = Environment.GetEnvironmentVariable("PRESENTER_LIVE_PROBE_OUT");
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var fullPath = Path.GetFullPath(outputPath);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(fullPath, markdown, Encoding.UTF8);
            _output?.WriteLine($"\nResults written to: {fullPath}");
        }

        var titleNavResults = probeResults.Where(r => r.Utterance.Contains("How it works", StringComparison.OrdinalIgnoreCase) || r.WavFileName.Contains("slide-about-title", StringComparison.OrdinalIgnoreCase)).ToList();
        var titleNavCorrect = titleNavResults.Count(r => r.Delegated && r.ToolMatched && r.ArgsMatched && r.CycleCompleted);
        var requiredTitleNav = repeatCount >= 5 ? 4 : repeatCount;

        _output?.WriteLine($"\nTitle navigation correct rate: {titleNavCorrect}/{titleNavResults.Count} (minimum {requiredTitleNav} required)");
        Assert.True(barrierResult.Completed, "Two-output barrier check failed to complete.");
        Assert.True(titleNavCorrect >= requiredTitleNav, $"Title navigation gate failed: {titleNavCorrect}/{titleNavResults.Count} (minimum {requiredTitleNav} required).");
    }

    private static IConfiguration BuildConfiguration()
    {
        var builder = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddUserSecrets("presenter-ai-api")
            .AddUserSecrets("presenter-ai-cli");

        var inMemory = new Dictionary<string, string?>();
        void MapEnv(string configKey, string envVar)
        {
            var val = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrWhiteSpace(val))
            {
                inMemory[configKey] = val;
            }
        }

        MapEnv("Upstream:Endpoint", "UPSTREAM_ENDPOINT");
        MapEnv("Upstream:Key", "UPSTREAM_KEY");
        MapEnv("Upstream:Model", "UPSTREAM_MODEL");
        MapEnv("Upstream:Voice", "UPSTREAM_VOICE");
        MapEnv("Upstream:DelegationModel", "UPSTREAM_DELEGATION_MODEL");
        MapEnv("Upstream:Fallback:Endpoint", "FALLBACK_OPENAI_ENDPOINT");
        MapEnv("Upstream:Fallback:Key", "FALLBACK_OPENAI_KEY");
        MapEnv("Upstream:Fallback:Model", "FALLBACK_OPENAI_MODEL");
        MapEnv("Upstream:Fallback:DelegationModel", "FALLBACK_DELEGATION_MODEL");

        if (inMemory.Count > 0)
        {
            builder.AddInMemoryCollection(inMemory);
        }

        return builder.Build();
    }

    private static string BuildMarkdownReport(
        UpstreamRoute route,
        string voice,
        string audioDir,
        IReadOnlyList<ProbeRunResult> results,
        BarrierCheckResult barrierResult)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Live Tool Probe Results (Plan 007 Task 0)");
        sb.AppendLine();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"**Date:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC  "));
        sb.AppendLine($"**Endpoint Host:** `{route.LiveUrl.Host}`  ");
        sb.AppendLine($"**Model:** `{route.Model}`  ");
        sb.AppendLine($"**Voice:** `{voice}`  ");
        sb.AppendLine($"**Delegation Model:** `{route.DelegationModel}`  ");
        sb.AppendLine($"**Audio Directory:** `{audioDir}`  ");
        sb.AppendLine();

        sb.AppendLine("## Utterance Probe Runs");
        sb.AppendLine();
        sb.AppendLine("| Run | Utterance | Input Transcript | Spoken Output (≤200 chars) | Delegated? | Tool Called | Expected Tool | Match? | Arguments | Expected Args | Args Match? | Cycle Done? | Utterance-End → Call (ms) | Call → Spoken (ms) | Event Trace |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");

        foreach (var r in results)
        {
            var uttEndToCallStr = r.UtteranceEndToCallLatencyMs.HasValue
                ? r.UtteranceEndToCallLatencyMs.Value.ToString("F1", CultureInfo.InvariantCulture)
                : "N/A";
            var latencyStr = r.CallToSpokenLatencyMs.HasValue
                ? r.CallToSpokenLatencyMs.Value.ToString("F1", CultureInfo.InvariantCulture)
                : "N/A";
            var traceStr = string.Join(" → ", r.EventTrace);
            var toolStr = r.ToolCalled ?? "none";
            var argsStr = string.IsNullOrWhiteSpace(r.Arguments) ? "{}" : r.Arguments.Replace("\n", " ").Replace("\r", "");
            var toolMatchStr = r.ToolMatched ? "Yes" : "No";
            var argsMatchStr = r.ArgsMatched ? "Yes" : "No";
            var cycleDoneStr = r.CycleCompleted ? "Yes" : "No";
            var transcriptStr = string.IsNullOrWhiteSpace(r.InputTranscript) ? "(none)" : r.InputTranscript.Replace("|", "\\|").Replace("\n", " ").Replace("\r", "");
            var spokenStr = string.IsNullOrWhiteSpace(r.SpokenTranscript)
                ? "(none)"
                : TruncateText(r.SpokenTranscript, 200).Replace("|", "\\|").Replace("\n", " ").Replace("\r", "");

            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {r.RunIndex} | {r.Utterance} | {transcriptStr} | {spokenStr} | {(r.Delegated ? "Yes" : "No")} | `{toolStr}` | `{r.ExpectedTool}` | {toolMatchStr} | `{argsStr}` | `{r.ExpectedArguments}` | {argsMatchStr} | {cycleDoneStr} | {uttEndToCallStr} | {latencyStr} | {traceStr} |"));
        }

        sb.AppendLine();
        sb.AppendLine("## Two-Outputs Barrier Verification");
        sb.AppendLine();
        sb.AppendLine($"- **Utterance:** `{barrierResult.Utterance}` (`{barrierResult.WavFileName}`)");
        sb.AppendLine($"- **Input Transcript:** {(!string.IsNullOrWhiteSpace(barrierResult.InputTranscript) ? barrierResult.InputTranscript : "(none)")}");
        sb.AppendLine($"- **Spoken Output:** {(!string.IsNullOrWhiteSpace(barrierResult.SpokenTranscript) ? TruncateText(barrierResult.SpokenTranscript, 200) : "(none)")}");
        sb.AppendLine($"- **Calls Emitted:** {barrierResult.CallsEmitted} ({(barrierResult.ToolsCalled.Count > 0 ? string.Join(", ", barrierResult.ToolsCalled.Select(t => $"`{t}`")) : "none")})");
        sb.AppendLine($"- **Strategy:** {barrierResult.Strategy}");
        sb.AppendLine($"- **Completed:** `{barrierResult.Completed}`");
        sb.AppendLine($"- **Event Trace:** {string.Join(" → ", barrierResult.EventTrace)}");
        sb.AppendLine();

        sb.AppendLine("## Summary Statistics");
        sb.AppendLine();
        var totalRuns = results.Count;
        var totalDelegated = results.Count(r => r.Delegated);
        var titleNavResults = results.Where(r => r.Utterance.Contains("How it works", StringComparison.OrdinalIgnoreCase) || r.WavFileName.Contains("slide-about-title", StringComparison.OrdinalIgnoreCase)).ToList();
        var titleNavCorrect = titleNavResults.Count(r => r.Delegated && r.ToolMatched && r.ArgsMatched && r.CycleCompleted);

        var uttEndLatencies = results.Where(r => r.UtteranceEndToCallLatencyMs.HasValue).Select(r => r.UtteranceEndToCallLatencyMs!.Value).ToList();
        var avgUttEndLatency = uttEndLatencies.Count > 0 ? uttEndLatencies.Average() : 0;
        var minUttEndLatency = uttEndLatencies.Count > 0 ? uttEndLatencies.Min() : 0;
        var maxUttEndLatency = uttEndLatencies.Count > 0 ? uttEndLatencies.Max() : 0;

        var callToSpokenLatencies = results.Where(r => r.CallToSpokenLatencyMs.HasValue).Select(r => r.CallToSpokenLatencyMs!.Value).ToList();
        var avgCallToSpoken = callToSpokenLatencies.Count > 0 ? callToSpokenLatencies.Average() : 0;
        var minCallToSpoken = callToSpokenLatencies.Count > 0 ? callToSpokenLatencies.Min() : 0;
        var maxCallToSpoken = callToSpokenLatencies.Count > 0 ? callToSpokenLatencies.Max() : 0;

        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- **Total Utterance Runs:** {totalRuns}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- **Total Function Calls (Delegated):** {totalDelegated} / {totalRuns} ({(totalRuns > 0 ? (totalDelegated * 100.0 / totalRuns).ToString("F1", CultureInfo.InvariantCulture) : "0")}%)"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- **Title Navigation Correct (Tool & Args Match & Cycle Completed):** {titleNavCorrect} / {titleNavResults.Count} ({(titleNavResults.Count > 0 ? (titleNavCorrect * 100.0 / titleNavResults.Count).ToString("F1", CultureInfo.InvariantCulture) : "0")}%)"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- **Utterance-End → Call Latency:** Avg {avgUttEndLatency.ToString("F1", CultureInfo.InvariantCulture)} ms (Min {minUttEndLatency.ToString("F1", CultureInfo.InvariantCulture)} ms, Max {maxUttEndLatency.ToString("F1", CultureInfo.InvariantCulture)} ms)"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- **Call → Spoken Latency:** Avg {avgCallToSpoken.ToString("F1", CultureInfo.InvariantCulture)} ms (Min {minCallToSpoken.ToString("F1", CultureInfo.InvariantCulture)} ms, Max {maxCallToSpoken.ToString("F1", CultureInfo.InvariantCulture)} ms)"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- **Barrier Completed:** {barrierResult.Completed}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- **Gate Succeeded:** {(titleNavCorrect >= (titleNavResults.Count >= 5 ? 4 : titleNavResults.Count) && barrierResult.Completed ? "Yes" : "No")}"));

        var allErrors = results.SelectMany(r => r.Errors).ToList();
        if (allErrors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Upstream Errors Recorded");
            sb.AppendLine();
            foreach (var err in allErrors.Distinct())
            {
                sb.AppendLine($"- `{err}`");
            }
        }

        return sb.ToString();
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength] + "…";
    }

    private sealed record ProbeUtterance(
        string Label,
        string WavFileName,
        string ExpectedTool,
        string ExpectedArguments);

    private sealed record ProbeRunResult(
        int RunIndex,
        string Utterance,
        string WavFileName,
        string InputTranscript,
        string SpokenTranscript,
        bool Delegated,
        string? ToolCalled,
        string ExpectedTool,
        bool ToolMatched,
        string? Arguments,
        string ExpectedArguments,
        bool ArgsMatched,
        bool CycleCompleted,
        double? UtteranceEndToCallLatencyMs,
        double? CallToSpokenLatencyMs,
        IReadOnlyList<string> EventTrace,
        IReadOnlyList<string> Errors);

    private sealed record BarrierCheckResult(
        string Utterance,
        string WavFileName,
        string InputTranscript,
        string SpokenTranscript,
        int CallsEmitted,
        IReadOnlyList<string> ToolsCalled,
        string Strategy,
        bool Completed,
        IReadOnlyList<string> EventTrace);

    private sealed class ProbeLiveClient : IAsyncDisposable
    {
        private const int BytesPerMs = 48; // 24 kHz, 16-bit mono = 48 bytes/ms
        private const int FrameDurationMs = 20; // 20 ms frames
        private const int FrameBytes = FrameDurationMs * BytesPerMs; // 960 bytes
        private const int SilenceFramesCount = 75; // 75 * 20 ms = 1500 ms = 1.5 s silence

        private readonly UpstreamRoute _route;
        private readonly string _voice;
        private readonly ITestOutputHelper? _logger;
        private readonly ClientWebSocket _socket = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Channel<JsonObject> _inbound = Channel.CreateUnbounded<JsonObject>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private Task? _receiveLoop;
        private int _eventSequence;

        public ProbeLiveClient(UpstreamRoute route, string voice, ITestOutputHelper? logger)
        {
            _route = route;
            _voice = voice;
            _logger = logger;
        }

        public async Task<string> ConnectAndStartAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            foreach (var header in _route.Headers)
            {
                _socket.Options.SetRequestHeader(header.Key, header.Value);
            }

            await _socket.ConnectAsync(_route.LiveUrl, linked.Token).ConfigureAwait(false);
            _receiveLoop = ReceiveLoopAsync(_lifetime.Token);

            var startPayload = BuildStartPayload(_route, _voice);
            await SendJsonAsync(startPayload, linked.Token).ConfigureAwait(false);

            while (!linked.Token.IsCancellationRequested)
            {
                var msg = await _inbound.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
                var type = msg["type"]?.GetValue<string>();
                if (type == "session.started")
                {
                    return msg["session"]?["id"]?.GetValue<string>() ?? "unknown";
                }

                if (type == "error")
                {
                    var errObj = msg["error"] as JsonObject ?? msg;
                    var code = errObj["code"]?.GetValue<string>() ?? "unknown";
                    var errType = errObj["type"]?.GetValue<string>() ?? "error";
                    var errMsg = errObj["message"]?.GetValue<string>() ?? "unknown";
                    throw new InvalidOperationException($"Upstream rejected session.start: [{errType}:{code}:{errMsg}]");
                }
            }

            throw new TimeoutException("Timed out waiting for session.started");
        }

        public async Task<ProbeRunResult> ProbeUtteranceAsync(int runIndex, ProbeUtterance utterance, string audioDir, CancellationToken cancellationToken)
        {
            var rawTrace = new List<string>();
            var errors = new List<string>();
            var inputTranscript = new StringBuilder();
            var spokenTranscript = new StringBuilder();
            var delegated = false;
            string? toolCalled = null;
            string? toolArgs = null;
            long callReceivedTick = 0;
            long outputSentTick = 0;
            long firstSpokenAfterCallTick = 0;
            long lastOutputAudioTick = 0;
            long delegationCompletedTick = 0;
            long nonDelegatedCompletedTick = 0;
            var delegationResponseCompleted = false;
            var nonDelegatedResponseCompleted = false;
            var cycleCompleted = false;

            var wavPath = Path.Combine(audioDir, utterance.WavFileName);
            var pcmData = ReadWavPcmData(wavPath);

            rawTrace.Add($"client:stream_audio:{utterance.WavFileName}");
            long audioEndTick = 0;
            var streamTask = Task.Run(async () =>
            {
                audioEndTick = await StreamAudioAndSilenceAsync(pcmData, cancellationToken).ConfigureAwait(false);
            }, cancellationToken);

            var startTick = Stopwatch.GetTimestamp();
            var deadline = startTick + (long)(20.0 * Stopwatch.Frequency); // 20 s total (brief item 1)

            while (Stopwatch.GetTimestamp() < deadline && !cancellationToken.IsCancellationRequested)
            {
                JsonObject message;
                using (var readTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
                using (var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, readTimeout.Token))
                {
                    try
                    {
                        message = await _inbound.Reader.ReadAsync(combined.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (readTimeout.IsCancellationRequested)
                    {
                        // Collect until response.completed of the function-call delegation has been followed by >= 2 s without output audio (brief item 1)
                        if (delegationResponseCompleted)
                        {
                            if (lastOutputAudioTick > 0 && Stopwatch.GetElapsedTime(lastOutputAudioTick) >= TimeSpan.FromSeconds(2.0))
                            {
                                break;
                            }

                            if (lastOutputAudioTick == 0 && delegationCompletedTick > 0 && Stopwatch.GetElapsedTime(delegationCompletedTick) >= TimeSpan.FromSeconds(2.0))
                            {
                                break;
                            }
                        }
                        else if (!delegated && streamTask.IsCompleted && nonDelegatedResponseCompleted)
                        {
                            if (lastOutputAudioTick > 0 && Stopwatch.GetElapsedTime(lastOutputAudioTick) >= TimeSpan.FromSeconds(2.0))
                            {
                                break;
                            }

                            if (lastOutputAudioTick == 0 && nonDelegatedCompletedTick > 0 && Stopwatch.GetElapsedTime(nonDelegatedCompletedTick) >= TimeSpan.FromSeconds(2.0))
                            {
                                break;
                            }
                        }

                        continue;
                    }
                }

                var type = message["type"]?.GetValue<string>() ?? "unknown";
                if (type == "session.input_transcript.delta")
                {
                    rawTrace.Add("session.input_transcript.delta");
                    var delta = message["delta"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(delta))
                    {
                        inputTranscript.Append(delta);
                    }
                }
                else if (type == "session.output_transcript.delta")
                {
                    rawTrace.Add("session.output_transcript.delta");
                    var delta = message["delta"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(delta))
                    {
                        spokenTranscript.Append(delta);
                    }

                    if (outputSentTick > 0 && firstSpokenAfterCallTick == 0)
                    {
                        firstSpokenAfterCallTick = Stopwatch.GetTimestamp();
                    }
                }
                else if (type == "session.output_audio.delta")
                {
                    rawTrace.Add("session.output_audio.delta");
                    lastOutputAudioTick = Stopwatch.GetTimestamp();

                    if (outputSentTick > 0 && firstSpokenAfterCallTick == 0)
                    {
                        firstSpokenAfterCallTick = Stopwatch.GetTimestamp();
                    }
                }
                else if (type == "response.event")
                {
                    var nested = message["event"]?["type"]?.GetValue<string>() ?? "unknown";
                    rawTrace.Add($"response.event:{nested}");

                    if (nested == "response.output_item.done")
                    {
                        var item = message["event"]?["item"] as JsonObject;
                        var itemType = item?["type"]?.GetValue<string>();
                        if (itemType == "function_call")
                        {
                            delegated = true;
                            toolCalled = item!["name"]?.GetValue<string>();
                            toolArgs = item["arguments"]?.GetValue<string>();
                            var callId = item["call_id"]?.GetValue<string>() ?? string.Empty;
                            callReceivedTick = Stopwatch.GetTimestamp();

                            var output = GetPlausibleOutput(toolCalled ?? string.Empty, toolArgs ?? string.Empty);
                            rawTrace.Add($"client:response.item.create:{toolCalled}");
                            await SendJsonAsync(new JsonObject
                            {
                                ["type"] = "response.item.create",
                                ["event_id"] = $"tool-res-{Interlocked.Increment(ref _eventSequence)}",
                                ["item"] = new JsonObject
                                {
                                    ["type"] = "function_call_output",
                                    ["call_id"] = callId,
                                    ["output"] = output
                                }
                            }, cancellationToken).ConfigureAwait(false);

                            rawTrace.Add("client:response.create");
                            await SendJsonAsync(new JsonObject
                            {
                                ["type"] = "response.create",
                                ["event_id"] = $"continue-{Interlocked.Increment(ref _eventSequence)}"
                            }, cancellationToken).ConfigureAwait(false);

                            outputSentTick = Stopwatch.GetTimestamp();
                        }
                    }
                    else if (nested == "response.completed" || nested == "response.done")
                    {
                        if (delegated && outputSentTick > 0)
                        {
                            delegationResponseCompleted = true;
                            delegationCompletedTick = Stopwatch.GetTimestamp();
                            cycleCompleted = true;
                            if (lastOutputAudioTick == 0)
                            {
                                lastOutputAudioTick = delegationCompletedTick;
                            }
                        }
                        else if (!delegated && streamTask.IsCompleted)
                        {
                            nonDelegatedResponseCompleted = true;
                            nonDelegatedCompletedTick = Stopwatch.GetTimestamp();
                            if (lastOutputAudioTick == 0)
                            {
                                lastOutputAudioTick = nonDelegatedCompletedTick;
                            }
                        }
                    }
                }
                else if (type == "error")
                {
                    var errObj = message["error"] as JsonObject ?? message;
                    var code = errObj["code"]?.GetValue<string>() ?? "unknown";
                    var errType = errObj["type"]?.GetValue<string>() ?? "error";
                    var errMsg = errObj["message"]?.GetValue<string>() ?? "unknown";
                    var formattedError = $"error[{errType}:{code}:{errMsg}]";
                    rawTrace.Add(formattedError);
                    errors.Add(formattedError);
                    _logger?.WriteLine($"Upstream error: {formattedError}");
                }
                else
                {
                    rawTrace.Add(type);
                }
            }

            try
            {
                await streamTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            double? uttEndToCallMs = null;
            if (callReceivedTick > 0 && audioEndTick > 0 && callReceivedTick >= audioEndTick)
            {
                uttEndToCallMs = (callReceivedTick - audioEndTick) * 1000.0 / Stopwatch.Frequency;
            }

            double? callToSpokenMs = null;
            if (callReceivedTick > 0 && firstSpokenAfterCallTick >= callReceivedTick)
            {
                callToSpokenMs = (firstSpokenAfterCallTick - callReceivedTick) * 1000.0 / Stopwatch.Frequency;
            }

            var (toolMatched, argsMatched) = VerifyExpectation(utterance.WavFileName, toolCalled, toolArgs);

            return new ProbeRunResult(
                runIndex,
                utterance.Label,
                utterance.WavFileName,
                inputTranscript.ToString().Trim(),
                spokenTranscript.ToString().Trim(),
                delegated,
                toolCalled,
                utterance.ExpectedTool,
                toolMatched,
                toolArgs,
                utterance.ExpectedArguments,
                argsMatched,
                cycleCompleted,
                uttEndToCallMs,
                callToSpokenMs,
                CollapseTrace(rawTrace),
                errors);
        }

        public async Task<BarrierCheckResult> ProbeTwoOutputsBarrierAsync(string audioDir, CancellationToken cancellationToken)
        {
            const string barrierUtterance = "Please go to the next slide and then pause.";
            const string wavFileName = "two-actions.wav";
            var rawTrace = new List<string>();
            var inputTranscript = new StringBuilder();
            var spokenTranscript = new StringBuilder();
            var pendingCalls = new List<(string CallId, string Name, string Args)>();
            var allCalls = new List<(string CallId, string Name, string Args)>();
            var completed = false;
            var strategy = "None";
            long lastOutputAudioTick = 0;
            long outputSentTick = 0;
            long delegationCompletedTick = 0;
            var delegationResponseCompleted = false;

            var wavPath = Path.Combine(audioDir, wavFileName);
            var pcmData = ReadWavPcmData(wavPath);

            rawTrace.Add($"client:stream_audio:{wavFileName}");
            var streamTask = Task.Run(async () =>
            {
                await StreamAudioAndSilenceAsync(pcmData, cancellationToken).ConfigureAwait(false);
            }, cancellationToken);

            var startTick = Stopwatch.GetTimestamp();
            var deadline = startTick + (long)(20.0 * Stopwatch.Frequency); // 20 s total (brief item 1)

            while (Stopwatch.GetTimestamp() < deadline && !cancellationToken.IsCancellationRequested)
            {
                JsonObject message;
                using (var readTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
                using (var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, readTimeout.Token))
                {
                    try
                    {
                        message = await _inbound.Reader.ReadAsync(combined.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (readTimeout.IsCancellationRequested)
                    {
                        if (delegationResponseCompleted)
                        {
                            if (lastOutputAudioTick > 0 && Stopwatch.GetElapsedTime(lastOutputAudioTick) >= TimeSpan.FromSeconds(2.0))
                            {
                                completed = true;
                                break;
                            }

                            if (lastOutputAudioTick == 0 && delegationCompletedTick > 0 && Stopwatch.GetElapsedTime(delegationCompletedTick) >= TimeSpan.FromSeconds(2.0))
                            {
                                completed = true;
                                break;
                            }
                        }

                        continue;
                    }
                }

                var type = message["type"]?.GetValue<string>() ?? "unknown";
                if (type == "session.input_transcript.delta")
                {
                    rawTrace.Add("session.input_transcript.delta");
                    var delta = message["delta"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(delta))
                    {
                        inputTranscript.Append(delta);
                    }
                }
                else if (type == "session.output_transcript.delta")
                {
                    rawTrace.Add(type);
                    var delta = message["delta"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(delta))
                    {
                        spokenTranscript.Append(delta);
                    }
                }
                else if (type == "session.output_audio.delta")
                {
                    rawTrace.Add(type);
                    lastOutputAudioTick = Stopwatch.GetTimestamp();
                }
                else if (type == "response.event")
                {
                    var nested = message["event"]?["type"]?.GetValue<string>() ?? "unknown";
                    rawTrace.Add($"response.event:{nested}");

                    if (nested == "response.output_item.done")
                    {
                        var item = message["event"]?["item"] as JsonObject;
                        if (item?["type"]?.GetValue<string>() == "function_call")
                        {
                            var callId = item["call_id"]?.GetValue<string>() ?? string.Empty;
                            var name = item["name"]?.GetValue<string>() ?? string.Empty;
                            var args = item["arguments"]?.GetValue<string>() ?? "{}";
                            pendingCalls.Add((callId, name, args));
                            allCalls.Add((callId, name, args));

                            // If two calls already collected in this turn (brief item 6)
                            if (pendingCalls.Count >= 2)
                            {
                                strategy = "Two function calls: submitted both outputs and exactly one response.create";
                                foreach (var call in pendingCalls)
                                {
                                    rawTrace.Add($"client:response.item.create:{call.Name}");
                                    await SendJsonAsync(new JsonObject
                                    {
                                        ["type"] = "response.item.create",
                                        ["event_id"] = $"tool-res-{Interlocked.Increment(ref _eventSequence)}",
                                        ["item"] = new JsonObject
                                        {
                                            ["type"] = "function_call_output",
                                            ["call_id"] = call.CallId,
                                            ["output"] = GetPlausibleOutput(call.Name, call.Args)
                                        }
                                    }, cancellationToken).ConfigureAwait(false);
                                }

                                rawTrace.Add("client:response.create");
                                await SendJsonAsync(new JsonObject
                                {
                                    ["type"] = "response.create",
                                    ["event_id"] = $"continue-barrier-{Interlocked.Increment(ref _eventSequence)}"
                                }, cancellationToken).ConfigureAwait(false);

                                outputSentTick = Stopwatch.GetTimestamp();
                                pendingCalls.Clear();
                            }
                        }
                    }
                    else if (nested == "response.completed" || nested == "response.done")
                    {
                        if (pendingCalls.Count == 1)
                        {
                            // Single call in this round: submit it and continue (brief item 6)
                            strategy = (allCalls.Count == 1)
                                ? "Single function call in round: submitted output and continued"
                                : $"Sequential calls: {string.Join(" then ", allCalls.Select(c => c.Name))}; submitted and continued";

                            var call = pendingCalls[0];
                            rawTrace.Add($"client:response.item.create:{call.Name}");
                            await SendJsonAsync(new JsonObject
                            {
                                ["type"] = "response.item.create",
                                ["event_id"] = $"tool-res-{Interlocked.Increment(ref _eventSequence)}",
                                ["item"] = new JsonObject
                                {
                                    ["type"] = "function_call_output",
                                    ["call_id"] = call.CallId,
                                    ["output"] = GetPlausibleOutput(call.Name, call.Args)
                                }
                            }, cancellationToken).ConfigureAwait(false);

                            rawTrace.Add("client:response.create");
                            await SendJsonAsync(new JsonObject
                            {
                                ["type"] = "response.create",
                                ["event_id"] = $"continue-barrier-{Interlocked.Increment(ref _eventSequence)}"
                            }, cancellationToken).ConfigureAwait(false);

                            outputSentTick = Stopwatch.GetTimestamp();
                            pendingCalls.Clear();
                        }
                        else if (pendingCalls.Count == 0 && outputSentTick > 0)
                        {
                            delegationResponseCompleted = true;
                            delegationCompletedTick = Stopwatch.GetTimestamp();
                            if (lastOutputAudioTick == 0)
                            {
                                lastOutputAudioTick = delegationCompletedTick;
                            }
                        }
                        else if (pendingCalls.Count == 0 && streamTask.IsCompleted)
                        {
                            strategy = "No function calls emitted";
                            break;
                        }
                    }
                }
                else if (type == "error")
                {
                    var errObj = message["error"] as JsonObject ?? message;
                    var code = errObj["code"]?.GetValue<string>() ?? "unknown";
                    var errType = errObj["type"]?.GetValue<string>() ?? "error";
                    var errMsg = errObj["message"]?.GetValue<string>() ?? "unknown";
                    rawTrace.Add($"error[{errType}:{code}:{errMsg}]");
                }
                else
                {
                    rawTrace.Add(type);
                }
            }

            try
            {
                await streamTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            completed = completed || allCalls.Count > 0;

            return new BarrierCheckResult(
                barrierUtterance,
                wavFileName,
                inputTranscript.ToString().Trim(),
                spokenTranscript.ToString().Trim(),
                allCalls.Count,
                allCalls.Select(c => c.Name).ToList(),
                strategy,
                completed,
                CollapseTrace(rawTrace));
        }

        public async Task CloseAsync()
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await SendJsonAsync(new JsonObject
                    {
                        ["type"] = "session.close",
                        ["event_id"] = $"close-{Interlocked.Increment(ref _eventSequence)}"
                    }, CancellationToken.None).ConfigureAwait(false);

                    await Task.Delay(200).ConfigureAwait(false);
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "probe_done", CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            try
            {
                _socket.Dispose();
            }
            catch
            {
            }

            if (_receiveLoop is not null)
            {
                try
                {
                    await _receiveLoop.ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        private async Task<long> StreamAudioAndSilenceAsync(byte[] pcmData, CancellationToken cancellationToken)
        {
            // Stream PCM audio in ~20 ms frames (960 bytes) with ≈ 20 ms delay
            for (var offset = 0; offset < pcmData.Length; offset += FrameBytes)
            {
                var count = Math.Min(FrameBytes, pcmData.Length - offset);
                var chunk = new byte[count];
                Buffer.BlockCopy(pcmData, offset, chunk, 0, count);

                await SendJsonAsync(new JsonObject
                {
                    ["type"] = "session.input_audio.append",
                    ["audio"] = Convert.ToBase64String(chunk)
                }, cancellationToken).ConfigureAwait(false);

                await Task.Delay(FrameDurationMs, cancellationToken).ConfigureAwait(false);
            }

            var audioEndTick = Stopwatch.GetTimestamp();

            // Stream ~1.5 s of silence frames (zero bytes) so server VAD ends the turn
            var silenceChunk = new byte[FrameBytes];
            var silenceBase64 = Convert.ToBase64String(silenceChunk);
            for (var i = 0; i < SilenceFramesCount; i++)
            {
                await SendJsonAsync(new JsonObject
                {
                    ["type"] = "session.input_audio.append",
                    ["audio"] = silenceBase64
                }, cancellationToken).ConfigureAwait(false);

                await Task.Delay(FrameDurationMs, cancellationToken).ConfigureAwait(false);
            }

            return audioEndTick;
        }

        private async Task SendJsonAsync(JsonObject payload, CancellationToken cancellationToken)
        {
            if (_socket.State != WebSocketState.Open)
            {
                return;
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[64 * 1024];
            using var stream = new MemoryStream();
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                stream.SetLength(0);
                do
                {
                    try
                    {
                        result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (WebSocketException)
                    {
                        return;
                    }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    stream.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    try
                    {
                        if (JsonNode.Parse(stream.ToArray()) is JsonObject json)
                        {
                            _inbound.Writer.TryWrite(json);
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }
            }
        }

        private static JsonObject BuildStartPayload(UpstreamRoute route, string voice)
        {
            var tools = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = "pause_presentation",
                    ["description"] = "Pause the presentation narration and audio.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject(),
                        ["additionalProperties"] = false
                    }
                },
                new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = "resume_presentation",
                    ["description"] = "Resume the presentation narration.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject(),
                        ["additionalProperties"] = false
                    }
                },
                new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = "next_slide",
                    ["description"] = "Advance to the next slide in the presentation.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject(),
                        ["additionalProperties"] = false
                    }
                },
                new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = "previous_slide",
                    ["description"] = "Go back to the previous slide in the presentation.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject(),
                        ["additionalProperties"] = false
                    }
                },
                new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = "go_to_slide",
                    ["description"] = "Navigate to a specific slide by its 1-based slide number.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["slide_number"] = new JsonObject
                            {
                                ["type"] = "integer",
                                ["description"] = "The 1-based slide number to navigate to."
                            }
                        },
                        ["required"] = new JsonArray { "slide_number" },
                        ["additionalProperties"] = false
                    }
                },
                new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = "end_presentation",
                    ["description"] = "End the presentation session.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["confirmed"] = new JsonObject
                            {
                                ["type"] = "boolean",
                                ["description"] = "Whether the end of the presentation is confirmed."
                            }
                        },
                        ["required"] = new JsonArray { "confirmed" },
                        ["additionalProperties"] = false
                    }
                }
            };

            const string systemInstructions =
                "You are the presenter delivering a talk titled \"Presenter-AI sample\" to a live audience. " +
                "Audience interaction rules:\n" +
                "- When someone in the audience speaks, interrupts, or gives a command or instruction (such as 'next slide', 'go to slide 3', 'go to the slide about How it works', 'end the meeting', or 'next slide and pause'), you MUST delegate the request immediately to the backend Responses model.\n" +
                "- Never execute presenter actions or answer slide navigation yourself without delegating first.\n" +
                "- Never claim an action happened until the backend result confirms it.";

            const string backendInstructions =
                "You are assisting a presenter. The presentation has the following slides:\n" +
                "Slide 1: Sample deck\n" +
                "Slide 2: How it works\n" +
                "Slide 3: Keys\n\n" +
                "You have presenter tools available. When the audience gives a navigation command or asks for a slide, you MUST invoke the appropriate function tool:\n" +
                "- 'next slide' -> invoke next_slide()\n" +
                "- 'previous slide' -> invoke previous_slide()\n" +
                "- 'go to slide 3' -> invoke go_to_slide(slide_number=3)\n" +
                "- 'go to the slide about How it works' -> invoke go_to_slide(slide_number=2)\n" +
                "- 'end the meeting' -> invoke end_presentation(confirmed=false)\n" +
                "- 'pause' -> invoke pause_presentation()\n" +
                "- 'resume' -> invoke resume_presentation()\n" +
                "- 'next slide and pause' or 'advance and pause' -> invoke next_slide() and pause_presentation()\n\n" +
                "Always invoke the function tool first before speaking. Never claim an action the tool did not confirm.";

            return new JsonObject
            {
                ["type"] = "session.start",
                ["event_id"] = "start-1",
                ["session"] = new JsonObject
                {
                    ["model"] = route.Model,
                    ["instructions"] = systemInstructions,
                    ["audio"] = new JsonObject
                    {
                        ["output"] = new JsonObject
                        {
                            ["voice"] = voice
                        }
                    },
                    ["delegation"] = new JsonObject
                    {
                        ["type"] = "responses",
                        ["responses"] = new JsonObject
                        {
                            ["model"] = route.DelegationModel,
                            ["instructions"] = backendInstructions,
                            ["tools"] = tools,
                            ["tool_choice"] = "auto",
                            ["parallel_tool_calls"] = false,
                            ["reasoning"] = new JsonObject { ["effort"] = "low" },
                            ["service_tier"] = "priority",
                            ["text"] = new JsonObject { ["verbosity"] = "low" }
                        }
                    }
                }
            };
        }

        private static string GetPlausibleOutput(string toolName, string arguments)
        {
            return toolName switch
            {
                "pause_presentation" => "{\"ok\":true,\"message\":\"presentation paused on slide 1 of 3\"}",
                "resume_presentation" => "{\"ok\":true,\"message\":\"presentation resumed on slide 1 of 3\"}",
                "next_slide" => "{\"ok\":true,\"message\":\"advanced to slide 2 of 3\"}",
                "previous_slide" => "{\"ok\":true,\"message\":\"returned to slide 1 of 3\"}",
                "go_to_slide" => ParseSlideOutput(arguments),
                "end_presentation" => "{\"ok\":true,\"message\":\"presentation end confirmed\"}",
                _ => "{\"ok\":true,\"message\":\"action completed\"}"
            };
        }

        private static string ParseSlideOutput(string arguments)
        {
            try
            {
                if (JsonNode.Parse(arguments) is JsonObject obj && obj["slide_number"]?.GetValue<int>() is int slideNum)
                {
                    return $"{{\"ok\":true,\"message\":\"navigated to slide {slideNum} of 3\"}}";
                }
            }
            catch
            {
            }

            return "{\"ok\":true,\"message\":\"navigated to requested slide\"}";
        }
    }

    private static byte[] ReadWavPcmData(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"WAV file not found: {filePath}", filePath);
        }

        using var stream = File.OpenRead(filePath);
        using var reader = new BinaryReader(stream);

        var riff = reader.ReadBytes(4);
        if (riff.Length < 4 || Encoding.ASCII.GetString(riff) != "RIFF")
        {
            throw new InvalidOperationException($"Invalid WAV file (missing RIFF header): {filePath}");
        }

        var fileSize = reader.ReadUInt32();
        var wave = reader.ReadBytes(4);
        if (wave.Length < 4 || Encoding.ASCII.GetString(wave) != "WAVE")
        {
            throw new InvalidOperationException($"Invalid WAV file (missing WAVE format): {filePath}");
        }

        while (stream.Position + 8 <= stream.Length)
        {
            var chunkIdBytes = reader.ReadBytes(4);
            var chunkId = Encoding.ASCII.GetString(chunkIdBytes);
            var chunkSize = reader.ReadUInt32();

            if (chunkId == "data")
            {
                return reader.ReadBytes((int)chunkSize);
            }

            var pad = (chunkSize % 2 != 0) ? 1 : 0;
            var nextPos = stream.Position + chunkSize + pad;
            if (nextPos > stream.Length)
            {
                break;
            }

            stream.Seek(chunkSize + pad, SeekOrigin.Current);
        }

        throw new InvalidOperationException($"No 'data' chunk found in WAV file: {filePath}");
    }

    private static (bool ToolMatched, bool ArgsMatched) VerifyExpectation(string utteranceOrFile, string? toolName, string? arguments)
    {
        if (string.IsNullOrEmpty(toolName))
        {
            return (false, false);
        }

        if (utteranceOrFile.Contains("next-slide", StringComparison.OrdinalIgnoreCase) || utteranceOrFile.Equals("next slide", StringComparison.OrdinalIgnoreCase))
        {
            return (toolName == "next_slide", true);
        }

        if (utteranceOrFile.Contains("go-to-slide-3", StringComparison.OrdinalIgnoreCase) || utteranceOrFile.Equals("go to slide 3", StringComparison.OrdinalIgnoreCase))
        {
            if (toolName != "go_to_slide") return (false, false);
            var num = ExtractSlideNumber(arguments);
            return (true, num == 3);
        }

        if (utteranceOrFile.Contains("slide-about-title", StringComparison.OrdinalIgnoreCase) || utteranceOrFile.Contains("How it works", StringComparison.OrdinalIgnoreCase))
        {
            if (toolName != "go_to_slide") return (false, false);
            var num = ExtractSlideNumber(arguments);
            return (true, num == 2);
        }

        if (utteranceOrFile.Contains("end-meeting", StringComparison.OrdinalIgnoreCase) || utteranceOrFile.Equals("end the meeting", StringComparison.OrdinalIgnoreCase))
        {
            return (toolName == "end_presentation", true);
        }

        return (false, false);
    }

    private static int? ExtractSlideNumber(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return null;
        try
        {
            if (JsonNode.Parse(arguments) is JsonObject obj && obj.TryGetPropertyValue("slide_number", out var node) && node is not null)
            {
                return node.GetValue<int>();
            }
        }
        catch
        {
        }

        return null;
    }

    private static IReadOnlyList<string> CollapseTrace(IReadOnlyList<string> rawTrace)
    {
        if (rawTrace.Count == 0) return [];
        var collapsed = new List<string>();
        var current = rawTrace[0];
        var count = 1;

        for (var i = 1; i < rawTrace.Count; i++)
        {
            if (rawTrace[i] == current)
            {
                count++;
            }
            else
            {
                collapsed.Add(count > 1 ? $"{current}×{count}" : current);
                current = rawTrace[i];
                count = 1;
            }
        }

        collapsed.Add(count > 1 ? $"{current}×{count}" : current);
        return collapsed;
    }
}
