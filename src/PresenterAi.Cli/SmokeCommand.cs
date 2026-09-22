using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Application.Presenting;
using PresenterAi.Infrastructure.Live;

namespace PresenterAi.Cli;

internal static class SmokeCommand
{
    private const string Instruction = "Say exactly: \"Presenter AI smoke test successful, one two three four five.\" Then stop.";

    public static async Task<int> RunAsync(
        SmokeArguments arguments,
        IConfiguration configuration,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        await using var services = Program.BuildServices(configuration, Directory.GetCurrentDirectory());
        _ = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<UpstreamOptions>>().Value;
        var routes = services.GetRequiredService<UpstreamRoutes>();
        var route = arguments.Provider == "azure"
            ? routes.Upstreams.FirstOrDefault()
            : routes.Upstreams.FirstOrDefault(candidate => candidate.Name == "fallback"
                || candidate.LiveUrl.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase));

        if (route is null)
        {
            await error.WriteLineAsync($"Provider not configured: {arguments.Provider}").ConfigureAwait(false);
            return 2;
        }

        var factory = services.GetRequiredService<ILiveSessionFactory>();
        var session = factory.Create(
            route,
            new LiveSessionConfig(route.Model, "You are a test presenter. Speak only when instructed, briefly, in English.", routes.Voice));

        var transcript = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();
        var startTick = Stopwatch.GetTimestamp();
        var audioDeltas = 0;
        var voicedDeltas = 0;
        var voicedBytes = 0;
        long firstVoicedTick = 0;
        long lastVoicedTick = 0;
        double? lastUsage = null;

        session.Audio += (bytes, _, _) =>
        {
            audioDeltas++;
            if (!AudioLevel.IsVoiced(bytes.Span))
            {
                return;
            }

            voicedDeltas++;
            voicedBytes += bytes.Length;
            var now = Stopwatch.GetTimestamp();
            firstVoicedTick = firstVoicedTick == 0 ? now : firstVoicedTick;
            lastVoicedTick = now;
        };
        session.Transcript += (role, delta, _, _) =>
        {
            if (role == "assistant")
            {
                transcript.Append(delta);
            }
        };
        session.Usage += (seconds, _) => lastUsage = seconds;

        try
        {
            var started = await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"session.started: id={started.Id ?? "unknown"} (+{stopwatch.ElapsedMilliseconds}ms)").ConfigureAwait(false);
            if (session.AppendInstructions(Instruction, "smoke-1") is null)
            {
                await error.WriteLineAsync("Smoke failed: could not append instructions.").ConfigureAwait(false);
                return 1;
            }

            var deadline = stopwatch.Elapsed + TimeSpan.FromSeconds(20);
            while (stopwatch.Elapsed < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                if (lastVoicedTick != 0 && Stopwatch.GetElapsedTime(lastVoicedTick) >= TimeSpan.FromSeconds(3))
                {
                    break;
                }
            }

            var close = await session.CloseAsync().ConfigureAwait(false);
            var seconds = close.Seconds ?? lastUsage;
            await output.WriteLineAsync($"closed: reason={close.Reason} usage={Format(seconds)}s").ConfigureAwait(false);
            var firstAt = firstVoicedTick == 0
                ? 0
                : (long)((firstVoicedTick - startTick) * 1000d / Stopwatch.Frequency);
            var voicedSeconds = voicedBytes / 48000d;
            await output.WriteLineAsync($"audio: {audioDeltas} deltas, {voicedSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s voiced, first at +{firstAt}ms").ConfigureAwait(false);
            await output.WriteLineAsync($"transcript: {transcript.ToString().Trim()}").ConfigureAwait(false);
            await output.WriteLineAsync($"usage.seconds={Format(seconds)}").ConfigureAwait(false);

            if (voicedDeltas == 0 || !close.Reason.Contains("request", StringComparison.OrdinalIgnoreCase))
            {
                await error.WriteLineAsync($"Smoke failed: voiced audio={voicedDeltas}, close reason={close.Reason}.").ConfigureAwait(false);
                return 1;
            }

            return 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync($"Smoke failed: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            if (session is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static string Format(double? value) =>
        value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unconfirmed";
}
