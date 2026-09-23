using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Application.Tools.External;
using PresenterAi.Cli;
using PresenterAi.Application.Presenting;
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
    public async Task Cli_has_no_session_tool_source_and_its_talk_has_no_external_tools()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        var configuration = Configuration(fake, new Dictionary<string, string?> { ["Upstream:DelegationModel"] = "managed" });
        using (var services = Program.BuildServices(configuration, FindRepositoryRoot()))
            services.GetService<ISessionToolSource>().Should().BeNull();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await Program.RunAsync(["run", "sample", "--stop-after-slide", "1", "--content-root", FindRepositoryRoot()], configuration, output, error, CancellationToken.None);
        exit.Should().Be(0, error.ToString());
        var starts = fake.ReceivedSnapshot().Where(message => message["type"]?.GetValue<string>() == "session.start").ToArray();
        starts.Should().NotBeEmpty();
        foreach (var start in starts)
        {
            var tools = start["session"]?["delegation"]?["responses"]?["tools"]?.AsArray();
            (tools?.Any(t => t?["type"]?.ToString() == "web_search" || t?["name"]?.ToString() == "external_action") ?? false).Should().BeFalse();
        }
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
            .And.Contain("closed reason=client_request end=stop_after_slide seconds=7");
        text.Should().NotContain("===== SLIDE 4 =====");
    }

    [Fact]
    public async Task Cancel_mid_talk_closes_the_upstream_before_max_seconds()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        var configuration = Configuration(fake, new Dictionary<string, string?> { ["Presenter:AdvanceSilenceMs"] = "10000" });
        using var cancellation = new CancellationTokenSource();
        var output = new StringWriter();
        var run = Program.RunAsync(
            ["run", "sample", "--max-seconds", "300", "--content-root", FindRepositoryRoot()],
            configuration, output, new StringWriter(), cancellation.Token);

        await WaitUntilAsync(() => output.ToString().Contains("===== SLIDE 1 =====", StringComparison.Ordinal));
        cancellation.Cancel();
        var exit = await run.WaitAsync(TimeSpan.FromSeconds(10));

        exit.Should().Be(1);
        output.ToString().Should().Contain("closed reason=client_request end=cli_cancelled");
        await WaitUntilAsync(() => fake.ConnectionCount == 0);
        fake.ReceivedSnapshot().Where(message => message["type"]?.GetValue<string>() == "session.close").Should().NotBeEmpty();
    }

    [Fact]
    public async Task Cancel_during_startup_leaves_no_upstream()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        fake.StartDelayMs = 30_000;
        var configuration = Configuration(fake, new Dictionary<string, string?>());
        using var cancellation = new CancellationTokenSource();
        var output = new StringWriter();
        var run = Program.RunAsync(
            ["run", "sample", "--max-seconds", "300", "--content-root", FindRepositoryRoot()],
            configuration, output, new StringWriter(), cancellation.Token);

        await WaitUntilAsync(() => fake.ReceivedSnapshot().Any(message => message["type"]?.GetValue<string>() == "session.start"));
        cancellation.Cancel();
        var exit = await run.WaitAsync(TimeSpan.FromSeconds(10));

        exit.Should().Be(1);
        output.ToString().Should().Contain("end=cli_cancelled");
        await WaitUntilAsync(() => fake.ConnectionCount == 0);
    }

    [Fact]
    public async Task Max_seconds_above_the_ceiling_is_clamped_with_a_warning()
    {
        await using var fake = await FakeLiveServer.StartAsync();
        var configuration = Configuration(fake, new Dictionary<string, string?>
        {
            ["Presenter:MaxTalkCeilingMinutes"] = "5",
            ["Presenter:MaxTalkMinutes"] = "5",
            ["Presenter:AdvanceSilenceMs"] = "100"
        });
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await Program.RunAsync(
            ["run", "sample", "--max-seconds", "600", "--stop-after-slide", "1", "--content-root", FindRepositoryRoot()],
            configuration, output, error, CancellationToken.None);

        exit.Should().Be(0, error.ToString());
        output.ToString().Should().Contain("Warning: --max-seconds 600 clamped to 300")
            .And.Contain("end=stop_after_slide");
        await WaitUntilAsync(() => fake.ConnectionCount == 0);
    }

    [Fact]
    public async Task Run_with_throwing_close_exits_1_and_writes_error()
    {
        await using var presenter = new ThrowingClosePresenter();
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await RunCommand.RunWithPresenterAsync(
            new RunArguments("sample", 60, 1, null), presenter, output, error, CancellationToken.None);

        exit.Should().Be(1);
        error.ToString().Should().Contain("Run failed: close failed");
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

    [Theory]
    [InlineData("Npgsql")]
    [InlineData("retry")]
    public void Database_connectivity_failures_are_classified_for_the_command_boundary(string kind)
    {
        Exception exception = kind == "Npgsql"
            ? new Npgsql.NpgsqlException("unreachable")
            : new Microsoft.EntityFrameworkCore.Storage.RetryLimitExceededException("retry limit", new Npgsql.NpgsqlException("unreachable"));

        Program.IsDatabaseConnectivityFailure(exception).Should().BeTrue();
    }

    [Fact]
    public async Task Import_without_owner_exits_2()
    {
        var error = new StringWriter();
        var exit = await Program.RunAsync(
            ["import", "presentations/sample.md"],
            new ConfigurationBuilder().Build(),
            new StringWriter(),
            error,
            CancellationToken.None);

        exit.Should().Be(2);
        error.ToString().Should().Contain("import requires --owner");
    }

    [Fact]
    public async Task Import_without_paths_exits_2()
    {
        var error = new StringWriter();
        var exit = await Program.RunAsync(
            ["import", "--owner", "owner@example.test"],
            new ConfigurationBuilder().Build(),
            new StringWriter(),
            error,
            CancellationToken.None);

        exit.Should().Be(2);
        error.ToString().Should().Contain("import requires at least one path");
    }

    [Fact]
    public async Task Run_without_owner_value_exits_2()
    {
        var error = new StringWriter();
        var exit = await Program.RunAsync(
            ["run", "sample", "--owner"],
            new ConfigurationBuilder().Build(),
            new StringWriter(),
            error,
            CancellationToken.None);

        exit.Should().Be(2);
        error.ToString().Should().Contain("--owner requires a value");
    }

    [Fact]
    public async Task Usage_lists_import_and_optional_run_owner()
    {
        var output = new StringWriter();
        var exit = await Program.RunAsync(
            ["--help"],
            new ConfigurationBuilder().Build(),
            output,
            new StringWriter(),
            CancellationToken.None);

        exit.Should().Be(0);
        output.ToString().Should().Contain("presenter-cli import").And.Contain("--owner");
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

    private sealed class ThrowingClosePresenter : IPresenter
    {
        public event Action<PresenterSnapshot>? State;
        public event Action<int>? Slide;
        public event Action<PresenterAudio>? Audio { add { } remove { } }
        public event Action<PresenterTranscript>? Transcript { add { } remove { } }
        public event Action<PresenterUsage>? Usage { add { } remove { } }
        public event Action<PresenterClosed>? Closed { add { } remove { } }
        public event Action<PresenterLog>? Log { add { } remove { } }
        public event Action<PresenterUpstreamError>? UpstreamError { add { } remove { } }

        public PresenterSnapshot Snapshot() => new("presenting", "sample", "sample", 0, 3, false, false, "test", null, 0, 200);

        public Task<PresenterStartResult> StartAsync(string id, int? fromIndex, string ownerId, CancellationToken cancellationToken = default)
        {
            State?.Invoke(Snapshot());
            Slide?.Invoke(1);
            return Task.FromResult(new PresenterStartResult(true, id, "test", "test", "test-model"));
        }

        public Task<bool> NextAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> PrevAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> GotoAsync(int index, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> PauseAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> ResumeAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> MuteAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> UnmuteAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> EndAsync(bool resumable = false, CancellationToken cancellationToken = default) => Task.FromException<bool>(new InvalidOperationException("close failed"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for the expected CLI state.");
            }

            await Task.Delay(20);
        }
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
