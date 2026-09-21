using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PresenterAi.Infrastructure;

namespace PresenterAi.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var configuration = BuildConfiguration(args);
        return await RunAsync(args, configuration, Console.Out, Console.Error, CancellationToken.None).ConfigureAwait(false);
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
                RunArguments run => await RunCommand.RunAsync(run, configuration, output, error, cancellationToken).ConfigureAwait(false),
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Cancelled.").ConfigureAwait(false);
            return 1;
        }
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
        services.AddLiveSessions();
        services.AddPresenter();
        return services.BuildServiceProvider();
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
        output.WriteLine("  presenter-cli run <id> [--max-seconds N] [--stop-after-slide N] [--content-root DIR]");
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
            "run" => ParseRun(args[1..], error),
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

    private static RunArguments? ParseRun(string[] args, TextWriter error)
    {
        string? id = null;
        var maxSeconds = 300;
        var stopAfterSlide = 0;
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

            if (argument is "--max-seconds" or "--stop-after-slide" or "--content-root")
            {
                if (index + 1 >= args.Length)
                {
                    error.WriteLine($"{argument} requires a value");
                    return null;
                }

                var value = args[++index];
                if (argument == "--content-root")
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

        return new RunArguments(id, maxSeconds, stopAfterSlide, contentRoot);
    }
}

internal abstract record CliArguments;
internal sealed record SmokeArguments(string Provider) : CliArguments;
internal sealed record RunArguments(string Id, int MaxSeconds, int StopAfterSlide, string? ContentRoot) : CliArguments;
