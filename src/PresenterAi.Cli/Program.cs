using System.Data.Common;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using PresenterAi.Infrastructure;

namespace PresenterAi.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var configuration = BuildConfiguration(args);
        using var cancellation = new CancellationTokenSource();
        var cancelCount = 0;
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            if (Interlocked.Increment(ref cancelCount) == 1)
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            }
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            return await RunAsync(args, configuration, Console.Out, Console.Error, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    public static async Task<int> RunAsync(
        string[] args,
        IConfiguration configuration,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (args.Any(argument => argument is "--help" or "-h"))
        {
            WriteUsage(output);
            return 0;
        }

        var command = CliParser.Parse(args, error);
        if (command is null)
        {
            WriteUsage(error);
            return 2;
        }

        try
        {
            return command switch
            {
                SmokeArguments smoke => await SmokeCommand.RunAsync(smoke, configuration, output, error, cancellationToken).ConfigureAwait(false),
                AskProbeArguments askProbe => await AskProbeCommand.RunAsync(askProbe, configuration, output, error, cancellationToken).ConfigureAwait(false),
                TtsArguments tts => await TtsCommand.RunAsync(tts, configuration, output, error, cancellationToken).ConfigureAwait(false),
                ComposeAskWavArguments compose => await ComposeAskWavCommand.RunAsync(compose, output, error, cancellationToken).ConfigureAwait(false),
                AskProbeSummaryArguments summary => await AskProbeSummary.RunAsync(summary, output, error).ConfigureAwait(false),
                RunArguments run => await RunCommand.RunAsync(run, configuration, output, error, cancellationToken).ConfigureAwait(false),
                ImportArguments import => await ImportCommand.RunAsync(import, configuration, output, error, cancellationToken).ConfigureAwait(false),
                _ => 2
            };
        }
        catch (OptionsValidationException exception)
        {
            await error.WriteLineAsync($"Configuration invalid: {string.Join("; ", exception.Failures)}").ConfigureAwait(false);
            return 2;
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith("Missing required setting:", StringComparison.Ordinal))
        {
            await error.WriteLineAsync($"Configuration invalid: {exception.Message}").ConfigureAwait(false);
            return 2;
        }
        catch (Exception exception) when (FindPostgresException(exception) is { } postgres)
        {
            await error.WriteLineAsync($"Database error: {postgres.MessageText}").ConfigureAwait(false);
            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Cancelled.").ConfigureAwait(false);
            return 1;
        }
        catch (Exception exception) when (IsDatabaseConnectivityFailure(exception))
        {
            await error.WriteLineAsync("Database unavailable.").ConfigureAwait(false);
            return 1;
        }
    }

    internal static bool IsDatabaseFailure(Exception exception) =>
        FindPostgresException(exception) is not null || IsDatabaseConnectivityFailure(exception);

    internal static bool IsDatabaseConnectivityFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException or DbException or RetryLimitExceededException)
            {
                return true;
            }
        }

        return false;
    }

    private static PostgresException? FindPostgresException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }

    internal static ServiceProvider BuildImportServices(IConfiguration configuration, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Postgres")))
        {
            throw new InvalidOperationException("Missing required setting: ConnectionStrings:Postgres");
        }

        var effectiveConfiguration = configuration;
        if (configuration["Content:RootDir"] is null)
        {
            effectiveConfiguration = new ConfigurationBuilder()
                .AddConfiguration(configuration)
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Content:RootDir"] = "." })
                .Build();
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddPersistence(effectiveConfiguration);
        services.AddFileContent(effectiveConfiguration, contentRoot);
        services.AddFileImportSource();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    internal static ServiceProvider BuildRunServices(IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Postgres")))
        {
            throw new InvalidOperationException("Missing required setting: ConnectionStrings:Postgres");
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddUpstreamOptions(configuration);
        services.AddPersistence(configuration);
        services.AddLiveSessions();
        services.AddPresenter();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    internal static ServiceProvider BuildServices(IConfiguration configuration, string contentRoot)
    {
        var effectiveConfiguration = configuration;
        if (configuration["Content:RootDir"] is null)
        {
            effectiveConfiguration = new ConfigurationBuilder()
                .AddConfiguration(configuration)
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Content:RootDir"] = "." })
                .Build();
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddUpstreamOptions(effectiveConfiguration);
        services.AddFileContent(effectiveConfiguration, contentRoot);
        services.AddFileImportSource();
        services.AddLiveSessions();
        services.AddPresenter(fileBacked: true);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static IConfiguration BuildConfiguration(string[] args)
    {
        var configArguments = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (!CliParser.IsConfigurationArgument(args[index]))
            {
                continue;
            }

            configArguments.Add(args[index]);
            if (!args[index].Contains('=') && index + 1 < args.Length && !args[index + 1].StartsWith("-", StringComparison.Ordinal))
            {
                configArguments.Add(args[++index]);
            }
        }

        var builder = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddUserSecrets(typeof(Program).Assembly, optional: true);
        if (configArguments.Count > 0)
        {
            builder.AddCommandLine(configArguments.ToArray());
        }

        return builder.Build();
    }

    internal static void WriteUsage(TextWriter output)
    {
        output.WriteLine("Usage:");
        output.WriteLine("  presenter-cli smoke --provider azure|openai");
        output.WriteLine("  presenter-cli ask-probe --provider azure|openai --part1 <wav> --part2 <wav> [--gap-seconds 10] [--variant vad|continue|raw]");
        output.WriteLine("            [--tail-ms 1000] [--gap-keep-ms 320] [--reply <wav>] [--lang en|vi] [--observe-interrupt] [--absent <wav>] [--trace] [--pace F]");
        output.WriteLine("            WAVs are 24 kHz mono PCM16; prints transcripts, timings and usage, never audio or secrets.");
        output.WriteLine("  presenter-cli tts --out <wav> (--text <t> | --text-file <f>) [--provider azure|openai] [--model gpt-audio-1.5] [--voice marin]");
        output.WriteLine("  presenter-cli compose-ask-wav --question <wav> --out <wav> --kept-seconds N [--preamble <wav>]... [--part2 <wav>] [--gap-seconds 10]");
        output.WriteLine("  presenter-cli ask-probe-summary <log-dir>   (T1 suite table and verdict; exit 0 = T1 passes)");
        output.WriteLine("  presenter-cli run <id> [--owner <email>] [--max-seconds N] [--stop-after-slide N] [--content-root DIR]");
        output.WriteLine("  presenter-cli import <path-or-pattern>... --owner <email> [--content-root DIR]");
        output.WriteLine("  presenter-cli --help");
        output.WriteLine();
        output.WriteLine("Exit codes: 0 success, 1 upstream/run failure, 2 usage or configuration error.");
    }
}

internal static class CliParser
{
    public static CliArguments? Parse(string[] args, TextWriter error)
    {
        if (args.Length == 0)
        {
            return null;
        }

        return args[0] switch
        {
            "smoke" => ParseSmoke(args[1..], error),
            "ask-probe" => ParseAskProbe(args[1..], error),
            "tts" => ParseTts(args[1..], error),
            "compose-ask-wav" => ParseCompose(args[1..], error),
            "ask-probe-summary" => ParseSummary(args[1..], error),
            "run" => ParseRun(args[1..], error),
            "import" => ParseImport(args[1..], error),
            _ => null
        };
    }

    public static bool IsConfigurationArgument(string argument) =>
        argument.StartsWith("--Upstream:", StringComparison.OrdinalIgnoreCase)
        || argument.StartsWith("--Presenter:", StringComparison.OrdinalIgnoreCase)
        || argument.StartsWith("--Content:", StringComparison.OrdinalIgnoreCase);

    private static SmokeArguments? ParseSmoke(string[] args, TextWriter error)
    {
        string? provider = null;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (IsConfigurationArgument(argument))
            {
                index += argument.Contains('=') || index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal) ? 0 : 1;
                continue;
            }

            if (argument == "--provider" && index + 1 < args.Length)
            {
                provider = args[++index];
                continue;
            }

            error.WriteLine($"Unknown smoke option: {argument}");
            return null;
        }

        if (provider is not ("azure" or "openai"))
        {
            error.WriteLine("smoke requires --provider azure|openai");
            return null;
        }

        return new SmokeArguments(provider);
    }

    private static AskProbeArguments? ParseAskProbe(string[] args, TextWriter error)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var observeInterrupt = false;
        var trace = false;
        string[] valued = ["--provider", "--part1", "--part2", "--gap-seconds", "--variant", "--tail-ms", "--gap-keep-ms", "--reply", "--lang", "--absent", "--pace"];
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (IsConfigurationArgument(argument))
            {
                index += argument.Contains('=') || index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal) ? 0 : 1;
                continue;
            }

            if (argument == "--observe-interrupt")
            {
                observeInterrupt = true;
                continue;
            }

            if (argument == "--trace")
            {
                trace = true;
                continue;
            }

            if (!valued.Contains(argument))
            {
                error.WriteLine($"Unknown ask-probe option: {argument}");
                return null;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error.WriteLine($"{argument} requires a value");
                return null;
            }

            values[argument] = args[++index];
        }

        var provider = values.GetValueOrDefault("--provider");
        if (provider is not ("azure" or "openai"))
        {
            error.WriteLine("ask-probe requires --provider azure|openai");
            return null;
        }

        if (!values.TryGetValue("--part1", out var part1) || !values.TryGetValue("--part2", out var part2))
        {
            error.WriteLine("ask-probe requires both --part1 <wav> and --part2 <wav>");
            return null;
        }

        var variant = values.GetValueOrDefault("--variant", "vad");
        if (variant is not ("vad" or "continue" or "raw"))
        {
            error.WriteLine("--variant must be vad, continue or raw");
            return null;
        }

        var lang = values.GetValueOrDefault("--lang", "en");
        if (lang is not ("en" or "vi"))
        {
            error.WriteLine("--lang must be en or vi");
            return null;
        }

        if (!double.TryParse(values.GetValueOrDefault("--gap-seconds", "10"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gapSeconds)
            || gapSeconds is < 0 or > 60)
        {
            error.WriteLine("--gap-seconds must be a number from 0 to 60");
            return null;
        }

        if (!int.TryParse(values.GetValueOrDefault("--tail-ms", "1000"), out var tailMs)
            || tailMs is < Application.Presenting.Asking.AskRecorder.MinTailSilenceMs or > Application.Presenting.Asking.AskRecorder.MaxTailSilenceMs)
        {
            error.WriteLine("--tail-ms must be an integer from 500 to 2000");
            return null;
        }

        if (!int.TryParse(values.GetValueOrDefault("--gap-keep-ms", "320"), out var gapKeepMs)
            || gapKeepMs is < Application.Presenting.Asking.AskRecorder.MinGapKeepMs or > Application.Presenting.Asking.AskRecorder.MaxGapKeepMs)
        {
            error.WriteLine("--gap-keep-ms must be an integer from 200 to 400");
            return null;
        }

        if (!double.TryParse(values.GetValueOrDefault("--pace", "0"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pace)
            || pace is < 0 or > 20)
        {
            error.WriteLine("--pace must be a number from 0 (unpaced burst) to 20");
            return null;
        }

        return new AskProbeArguments(provider, part1, part2, gapSeconds, variant, tailMs, gapKeepMs, observeInterrupt,
            values.GetValueOrDefault("--reply"), lang, values.GetValueOrDefault("--absent"), trace, pace);
    }

    private static Dictionary<string, List<string>>? ParseOptions(string[] args, string command, string[] valued, TextWriter error, List<string>? positional = null)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (IsConfigurationArgument(argument))
            {
                index += argument.Contains('=') || index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal) ? 0 : 1;
                continue;
            }

            if (positional is not null && !argument.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(argument);
                continue;
            }

            if (!valued.Contains(argument))
            {
                error.WriteLine($"Unknown {command} option: {argument}");
                return null;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error.WriteLine($"{argument} requires a value");
                return null;
            }

            if (!values.TryGetValue(argument, out var list))
            {
                values[argument] = list = [];
            }

            list.Add(args[++index]);
        }

        return values;
    }

    private static TtsArguments? ParseTts(string[] args, TextWriter error)
    {
        var values = ParseOptions(args, "tts", ["--provider", "--model", "--voice", "--text", "--text-file", "--out", "--attempts"], error);
        if (values is null)
        {
            return null;
        }

        string? One(string key) => values.TryGetValue(key, out var list) ? list[^1] : null;
        var provider = One("--provider") ?? "azure";
        if (provider is not ("azure" or "openai"))
        {
            error.WriteLine("tts requires --provider azure|openai");
            return null;
        }

        if ((One("--text") is null) == (One("--text-file") is null))
        {
            error.WriteLine("tts requires exactly one of --text <t> or --text-file <f>");
            return null;
        }

        if (One("--out") is not { } output)
        {
            error.WriteLine("tts requires --out <wav>");
            return null;
        }

        if (!int.TryParse(One("--attempts") ?? "3", out var attempts) || attempts is < 1 or > 10)
        {
            error.WriteLine("--attempts must be an integer from 1 to 10");
            return null;
        }

        return new TtsArguments(provider, One("--model") ?? "gpt-audio-1.5", One("--voice") ?? "marin", One("--text"), One("--text-file"), output, attempts);
    }

    private static ComposeAskWavArguments? ParseCompose(string[] args, TextWriter error)
    {
        var values = ParseOptions(args, "compose-ask-wav", ["--preamble", "--question", "--part2", "--kept-seconds", "--gap-seconds", "--out"], error);
        if (values is null)
        {
            return null;
        }

        string? One(string key) => values.TryGetValue(key, out var list) ? list[^1] : null;
        if (One("--question") is not { } question || One("--out") is not { } output)
        {
            error.WriteLine("compose-ask-wav requires --question <wav> and --out <wav>");
            return null;
        }

        if (!double.TryParse(One("--kept-seconds"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var kept) || kept is <= 0 or > 120)
        {
            error.WriteLine("compose-ask-wav requires --kept-seconds from 1 to 120");
            return null;
        }

        if (!double.TryParse(One("--gap-seconds") ?? "10", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gap) || gap is < 0 or > 60)
        {
            error.WriteLine("--gap-seconds must be a number from 0 to 60");
            return null;
        }

        return new ComposeAskWavArguments(values.TryGetValue("--preamble", out var preambles) ? preambles : [], question, One("--part2"), kept, gap, output);
    }

    private static AskProbeSummaryArguments? ParseSummary(string[] args, TextWriter error)
    {
        var positional = new List<string>();
        var values = ParseOptions(args, "ask-probe-summary", ["--cap-seconds"], error, positional);
        if (values is null)
        {
            return null;
        }

        if (positional.Count != 1)
        {
            error.WriteLine("ask-probe-summary requires one log directory");
            return null;
        }

        return new AskProbeSummaryArguments(positional[0]);
    }

    private static ImportArguments? ParseImport(string[] args, TextWriter error)
    {
        var paths = new List<string>();
        string? owner = null;
        string? contentRoot = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (IsConfigurationArgument(argument))
            {
                index += argument.Contains('=') || index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal) ? 0 : 1;
                continue;
            }

            if (argument is "--owner" or "--content-root")
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    error.WriteLine($"{argument} requires a value");
                    return null;
                }

                var value = args[++index];
                if (argument == "--owner") owner = value;
                else contentRoot = value;
                continue;
            }

            if (argument.StartsWith("-", StringComparison.Ordinal))
            {
                error.WriteLine($"Unknown import option: {argument}");
                return null;
            }

            paths.Add(argument);
        }

        if (paths.Count == 0)
        {
            error.WriteLine("import requires at least one path or pattern");
            return null;
        }

        if (string.IsNullOrWhiteSpace(owner))
        {
            error.WriteLine("import requires --owner <email>");
            return null;
        }

        return new ImportArguments(paths, owner, contentRoot);
    }

    private static RunArguments? ParseRun(string[] args, TextWriter error)
    {
        string? id = null;
        var maxSeconds = 300;
        var stopAfterSlide = 0;
        string? owner = null;
        string? contentRoot = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (IsConfigurationArgument(argument))
            {
                index += argument.Contains('=') || index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal) ? 0 : 1;
                continue;
            }

            if (!argument.StartsWith("-", StringComparison.Ordinal) && id is null)
            {
                id = argument;
                continue;
            }

            if (argument is "--owner" or "--max-seconds" or "--stop-after-slide" or "--content-root")
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    error.WriteLine($"{argument} requires a value");
                    return null;
                }

                var value = args[++index];
                if (argument == "--owner")
                {
                    owner = value;
                }
                else if (argument == "--content-root")
                {
                    contentRoot = value;
                }
                else if (!int.TryParse(value, out var number) || number < 1)
                {
                    error.WriteLine($"{argument} requires a positive integer");
                    return null;
                }
                else if (argument == "--max-seconds")
                {
                    maxSeconds = number;
                }
                else
                {
                    stopAfterSlide = number;
                }

                continue;
            }

            error.WriteLine($"Unknown run option: {argument}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            error.WriteLine("run requires a presentation id");
            return null;
        }

        return new RunArguments(id, maxSeconds, stopAfterSlide, contentRoot, owner);
    }
}

public abstract record CliArguments;
internal sealed record SmokeArguments(string Provider) : CliArguments;
internal sealed record AskProbeArguments(
    string Provider,
    string Part1,
    string Part2,
    double GapSeconds,
    string Variant,
    int TailMs,
    int GapKeepMs,
    bool ObserveInterrupt,
    string? Reply,
    string Lang,
    string? Absent,
    bool Trace = false,
    double Pace = 0) : CliArguments;
public sealed record RunArguments(string Id, int MaxSeconds, int StopAfterSlide, string? ContentRoot, string? Owner = null) : CliArguments;
public sealed record ImportArguments(IReadOnlyList<string> Paths, string Owner, string? ContentRoot) : CliArguments;
