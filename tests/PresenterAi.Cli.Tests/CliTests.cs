using FluentAssertions;
using Microsoft.Extensions.Configuration;
using PresenterAi.Cli;
using PresenterAi.Infrastructure.Tests.Live;
using Xunit;

namespace PresenterAi.Cli.Tests;

public sealed class CliTests
{
    [Fact]
    public async Task Smoke_selects_provider_and_reports_usage_seconds()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        var configuration = Configuration(fake, new Dictionary<string, string?>
        {
            ["Upstream:Model"] = "azure-model",
            ["Upstream:Fallback:Model"] = "openai-model"
        });

        var azureOutput = new StringWriter();
        var azureError = new StringWriter();
        var azureExit = await Program.RunAsync(
            ["smoke", "--provider", "azure"], configuration, azureOutput, azureError, CancellationToken.None);

        var openAiOutput = new StringWriter();
        var openAiError = new StringWriter();
        var openAiExit = await Program.RunAsync(
            ["smoke", "--provider", "openai"], configuration, openAiOutput, openAiError, CancellationToken.None);

        azureExit.Should().Be(0, azureError.ToString());
        openAiExit.Should().Be(0, openAiError.ToString());
        azureOutput.ToString().Should().Contain("usage.seconds=7").And.Contain(" s voiced");
        openAiOutput.ToString().Should().Contain("usage.seconds=7").And.Contain(" s voiced");

        var starts = fake.ReceivedSnapshot().Where(message => message["type"]?.GetValue<string>() == "session.start").ToArray();
        starts.Should().HaveCount(2);
        starts[0]["session"]!["model"]!.GetValue<string>().Should().Be("azure-model");
        starts[1]["session"]!["model"]!.GetValue<string>().Should().Be("openai-model");
        fake.Headers.Should().ContainKey("Authorization").And.NotContainKey("api-key");
    }

    [Fact]
    public async Task Run_sample_against_fake_server_stops_after_slide_2()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        var configuration = Configuration(fake, new Dictionary<string, string?>
        {
            ["Presenter:AdvanceSilenceMs"] = "200"
        });
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await Program.RunAsync(
            ["run", "sample", "--stop-after-slide", "2", "--content-root", FindRepositoryRoot()],
            configuration,
            output,
            error,
            CancellationToken.None);

        var text = output.ToString();
        exit.Should().Be(0, error.ToString());
        text.Should().Contain("===== SLIDE 1 =====")
            .And.Contain("===== SLIDE 2 =====")
            .And.Contain("===== SLIDE 3 =====")
            .And.Contain("stop-after-slide reached; ending")
            .And.Contain("MODEL:")
            .And.Contain("audio bar (100 ms/char): #")
            .And.Contain("closed reason=client_request seconds=7");
        text.Should().NotContain("===== SLIDE 4 =====");
    }

    [Fact]
    public async Task Missing_key_exits_2_with_one_line()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Upstream:Endpoint"] = "ws://127.0.0.1:1/v1/live/sessions"
        }).Build();
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await Program.RunAsync(
            ["smoke", "--provider", "azure"], configuration, output, error, CancellationToken.None);

        exit.Should().Be(2);
        error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Should().Equal("Configuration invalid: Missing required setting: Upstream:Key");
    }

    [Fact]
    public async Task Unknown_provider_exits_2()
    {
        var configuration = new ConfigurationBuilder().Build();
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await Program.RunAsync(
            ["smoke", "--provider", "google"], configuration, output, error, CancellationToken.None);

        exit.Should().Be(2);
        error.ToString().Should().Contain("smoke requires --provider azure|openai");
    }

    private static IConfiguration Configuration(FakeLiveServer fake, Dictionary<string, string?> overrides)
    {
        var values = new Dictionary<string, string?>(overrides)
        {
            ["Upstream:Endpoint"] = fake.Url,
            ["Upstream:Key"] = "k",
            ["Upstream:Fallback:Endpoint"] = fake.Url,
            ["Upstream:Fallback:Key"] = "sk-fb"
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PresenterAi.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("PresenterAi.slnx not found.");
    }
}
