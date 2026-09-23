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
public sealed record RunArguments(string Id, int MaxSeconds, int StopAfterSlide, string? ContentRoot, string? Owner = null) : CliArguments;
public sealed record ImportArguments(IReadOnlyList<string> Paths, string Owner, string? ContentRoot) : CliArguments;
