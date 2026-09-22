using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Application.Presenting;

namespace PresenterAi.Cli;

public static class RunCommand
{
    // The local content root has no owner boundary; the file-backed presenter loader intentionally ignores this.
    private const string LocalContentOwner = "local-file-content";

    public static async Task<int> RunAsync(
        RunArguments arguments,
        IConfiguration configuration,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var contentRoot = arguments.ContentRoot is null
            ? FindRepositoryRoot(Directory.GetCurrentDirectory())
            : Path.GetFullPath(arguments.ContentRoot, Directory.GetCurrentDirectory());

        await using var services = Program.BuildServices(configuration, contentRoot);
        return await RunWithPresenterAsync(arguments, services.GetRequiredService<IPresenter>(), output, error, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<int> RunWithPresenterAsync(
        RunArguments arguments,
        IPresenter presenter,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var closed = new TaskCompletionSource<PresenterClosed>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastRole = string.Empty;
        var line = new StringBuilder();
        var bar = new StringBuilder();
        var barMilliseconds = 0d;
        var voicedMilliseconds = 0d;
        var endRequested = 0;
        Task? endTask = null;

        string At() => $"+{stopwatch.Elapsed.TotalSeconds:0.0}s";

        void FlushLine()
        {
            if (line.Length == 0)
            {
                return;
            }

            output.WriteLine($"{At()} {(lastRole == "user" ? "YOU " : "MODEL")}: {line.ToString().Trim()}");
            line.Clear();
        }

        void FlushBar()
        {
            if (bar.Length == 0 && barMilliseconds <= 0)
            {
                return;
            }

            if (barMilliseconds > 0)
            {
                bar.Append(voicedMilliseconds > 0 ? '#' : '.');
            }

            var pauses = bar.ToString()
                .Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Length == 0
                ? Array.Empty<string>()
                : FindPauses(bar.ToString());
            output.WriteLine($"{At()} audio bar (100 ms/char): {bar.ToString().TrimEnd('.')}");
            output.WriteLine($"{At()} pauses ≥2s inside this slide: {(pauses.Length == 0 ? "none" : string.Join(", ", pauses))}");
            bar.Clear();
            barMilliseconds = 0;
            voicedMilliseconds = 0;
        }

        presenter.State += snapshot =>
            output.WriteLine($"{At()} state={snapshot.State} slide={snapshot.SlideIndex + 1}/{snapshot.SlideCount}{(snapshot.SessionId is null ? string.Empty : $" session={snapshot.SessionId}")}");
        Task RequestEndAsync()
        {
            var task = presenter.EndAsync(CancellationToken.None);
            Interlocked.CompareExchange(ref endTask, task, null);
            return task;
        }

        presenter.Slide += index =>
        {
            FlushLine();
            FlushBar();
            output.WriteLine($"{At()} ===== SLIDE {index + 1} =====");
            // Same semantics as the Node MVP's headless run (retired — see docs/reference/002-node-mvp-retired.md): the stop fires when slide N+1 is announced, so N slides are narrated.
            if (arguments.StopAfterSlide > 0 && index + 1 > arguments.StopAfterSlide && Interlocked.Exchange(ref endRequested, 1) == 0)
            {
                output.WriteLine($"{At()} stop-after-slide reached; ending");
                RequestEndAsync();
            }
        };
        presenter.Transcript += transcript =>
        {
            if (!string.Equals(transcript.Role, lastRole, StringComparison.Ordinal))
            {
                FlushLine();
                lastRole = transcript.Role;
            }

            line.Append(transcript.Delta);
        };
        presenter.Audio += audio =>
        {
            var milliseconds = audio.Bytes.Length / 48d;
            var voiced = AudioLevel.IsVoiced(audio.Bytes.Span);
            barMilliseconds += milliseconds;
            if (voiced)
            {
                voicedMilliseconds += milliseconds;
            }

            while (barMilliseconds >= 100)
            {
                bar.Append(voicedMilliseconds > 0 ? '#' : '.');
                barMilliseconds -= 100;
                voicedMilliseconds = 0;
            }
        };
        presenter.Log += log =>
        {
            if (!string.Equals(log.Level, "debug", StringComparison.OrdinalIgnoreCase))
            {
                output.WriteLine($"{At()} [{log.Level}] {log.Message}");
            }
        };
        presenter.Usage += usage => output.WriteLine($"{At()} usage seconds={Format(usage.Seconds)}");
        presenter.Closed += result =>
        {
            FlushLine();
            FlushBar();
            output.WriteLine($"{At()} closed reason={result.Reason} seconds={Format(result.Seconds)}");
            closed.TrySetResult(result);
        };
        presenter.UpstreamError += upstream =>
            output.WriteLine($"{At()} [ERROR] {upstream.Code ?? string.Empty} {upstream.Message}".TrimEnd());

        try
        {
            var start = await presenter.StartAsync(arguments.Id, fromIndex: null, ownerId: LocalContentOwner, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!start.Started)
            {
                await error.WriteLineAsync($"Run failed: could not start presentation \"{arguments.Id}\".").ConfigureAwait(false);
                return 1;
            }

            var timeout = Task.Delay(TimeSpan.FromSeconds(arguments.MaxSeconds), cancellationToken);
            var completed = endTask is null
                ? await Task.WhenAny(closed.Task, timeout).ConfigureAwait(false)
                : await Task.WhenAny(closed.Task, timeout, endTask).ConfigureAwait(false);
            if (completed == timeout && !closed.Task.IsCompleted && Interlocked.Exchange(ref endRequested, 1) == 0)
            {
                output.WriteLine($"{At()} max-seconds reached; ending");
                await RequestEndAsync().ConfigureAwait(false);
            }

            if (endTask is not null)
            {
                await endTask.ConfigureAwait(false);
            }

            PresenterClosed result;
            try
            {
                result = await closed.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await error.WriteLineAsync("Run failed: timed out waiting for session close.").ConfigureAwait(false);
                return 1;
            }

            return Presenter.IsNormalClose(result.Reason) ? 0 : 1;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync($"Run failed: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private static string[] FindPauses(string value)
    {
        var pauses = new List<string>();
        var start = -1;
        for (var index = 0; index <= value.Length; index++)
        {
            if (index < value.Length && value[index] == '.')
            {
                start = start < 0 ? index : start;
                continue;
            }

            if (start >= 0)
            {
                var length = index - start;
                if (length >= 20)
                {
                    pauses.Add((length / 10d).ToString("0.0", CultureInfo.InvariantCulture) + "s");
                }

                start = -1;
            }
        }

        return pauses.ToArray();
    }

    private static string Format(double? value) =>
        value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unconfirmed";

    private static string FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PresenterAi.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find PresenterAi.slnx above the current directory.");
    }
}
