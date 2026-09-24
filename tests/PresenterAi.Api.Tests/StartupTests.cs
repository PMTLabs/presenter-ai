using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Api.Tests.Infrastructure;

namespace PresenterAi.Api.Tests;

public sealed class StartupTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public void Container_validates_on_build()
    {
        var createClient = () => factory.CreateClient();

        createClient.Should().NotThrow();
    }

    [Fact]
    public void Missing_upstream_key_fails_startup()
    {
        using var missingKey = new ApiFactory
        {
            Overrides = new Dictionary<string, string?>
            {
                ["Upstream:Key"] = ""
            }
        };

        var createClient = () => missingKey.CreateClient();

        createClient.Should().Throw<Exception>()
            .Which.ToString().Should().Contain("Missing required setting: Upstream:Key");
    }

    [Theory]
    [InlineData("2499")]
    [InlineData("60001")]
    public void Out_of_range_follow_up_wait_fails_startup(string value)
    {
        using var factory = new ApiFactory
        {
            Overrides = new Dictionary<string, string?>
            {
                ["Presenter:FollowUpWaitMs"] = value
            }
        };

        var createClient = () => factory.CreateClient();

        createClient.Should().Throw<Exception>()
            .Which.ToString().Should().Contain("Presenter:FollowUpWaitMs must be between 2500 and 60000");
    }

    [Fact]
    public void Follow_up_wait_setting_reaches_the_presenter()
    {
        using var factory = new ApiFactory
        {
            Overrides = new Dictionary<string, string?>
            {
                ["Presenter:FollowUpWaitMs"] = "9000"
            }
        };

        var presenter = factory.Services.GetRequiredService<PresenterAi.Application.Presenting.IPresenter>();

        presenter.Should().BeOfType<PresenterAi.Application.Presenting.Presenter>()
            .Which.Settings.FollowUpWaitMs.Should().Be(9000);
    }

    [Fact]
    public void Production_container_uses_the_disabled_ask_transcriber()
    {
        // Plan 011 (G1-5): the ask transcriber port is registered disabled and is the one the presenter holds.
        var transcriber = factory.Services.GetRequiredService<PresenterAi.Application.Presenting.Asking.IAskTranscriber>();
        var presenter = factory.Services.GetRequiredService<PresenterAi.Application.Presenting.IPresenter>();

        transcriber.Should().BeOfType<PresenterAi.Application.Presenting.Asking.DisabledAskTranscriber>();
        transcriber.Begin("ask_1").Should().BeNull();
        presenter.Should().BeOfType<PresenterAi.Application.Presenting.Presenter>()
            .Which.AskTranscriber.Should().BeSameAs(transcriber);
    }

    [Theory]
    [InlineData("Presenter:MaxTalkMinutes", "4", "Presenter:MaxTalkMinutes")]
    [InlineData("Presenter:MaxTalkMinutes", "121", "Presenter:MaxTalkMinutes")]
    [InlineData("Presenter:MaxTalkCeilingMinutes", "4", "Presenter:MaxTalkCeilingMinutes")]
    [InlineData("Presenter:MaxTalkCeilingMinutes", "241", "Presenter:MaxTalkCeilingMinutes")]
    [InlineData("Presenter:MaxTalkCeilingMinutes", "50", "Presenter:MaxTalkMinutes")]
    [InlineData("Presenter:PauseGraceSeconds", "29", "Presenter:PauseGraceSeconds")]
    [InlineData("Presenter:PauseGraceSeconds", "901", "Presenter:PauseGraceSeconds")]
    [InlineData("Presenter:IdleTimeoutSeconds", "119", "Presenter:IdleTimeoutSeconds")]
    [InlineData("Presenter:IdleTimeoutSeconds", "1801", "Presenter:IdleTimeoutSeconds")]
    [InlineData("Session:HeartbeatIntervalSeconds", "4", "Session:HeartbeatIntervalSeconds")]
    [InlineData("Session:HeartbeatIntervalSeconds", "61", "Session:HeartbeatIntervalSeconds")]
    [InlineData("Session:HeartbeatTimeoutSeconds", "29", "Session:HeartbeatTimeoutSeconds")]
    [InlineData("Session:HeartbeatTimeoutSeconds", "301", "Session:HeartbeatTimeoutSeconds")]
    public void Out_of_range_talk_limit_fails_startup(string key, string value, string expectedError)
    {
        using var localFactory = new ApiFactory
        {
            Overrides = new Dictionary<string, string?>
            {
                [key] = value
            }
        };

        var createClient = () => localFactory.CreateClient();

        createClient.Should().Throw<Exception>()
            .Which.ToString().Should().Contain(expectedError);
    }

    [Theory]
    [InlineData("9")]
    [InlineData("181")]
    public void Out_of_range_reviser_timeout_fails_startup(string value)
    {
        using var localFactory = new ApiFactory
        {
            Overrides = new Dictionary<string, string?>
            {
                ["Training:ReviserTimeoutSeconds"] = value
            }
        };

        var createClient = () => localFactory.CreateClient();

        createClient.Should().Throw<Exception>()
            .Which.ToString().Should().Contain("Training:ReviserTimeoutSeconds must be between 10 and 180");
    }

    [Theory]
    [InlineData("10", 10)]
    [InlineData("180", 180)]
    public void Reviser_timeout_bounds_are_accepted_and_bound(string value, int expected)
    {
        using var localFactory = new ApiFactory
        {
            Overrides = new Dictionary<string, string?>
            {
                ["Training:ReviserTimeoutSeconds"] = value
            }
        };

        var options = localFactory.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<PresenterAi.Application.Scripts.Revisions.TrainingOptions>>();

        options.Value.ReviserTimeoutSeconds.Should().Be(expected);
    }

    [Fact]
    public void Talk_limit_settings_reach_the_presenter()
    {
        using var localFactory = new ApiFactory
        {
            Overrides = new Dictionary<string, string?>
            {
                ["Presenter:MaxTalkCeilingMinutes"] = "180",
                ["Presenter:MaxTalkMinutes"] = "90",
                ["Presenter:PauseGraceSeconds"] = "240",
                ["Presenter:IdleTimeoutSeconds"] = "600"
            }
        };

        var presenter = localFactory.Services.GetRequiredService<PresenterAi.Application.Presenting.IPresenter>();

        var settings = presenter.Should().BeOfType<PresenterAi.Application.Presenting.Presenter>()
            .Which.Settings;

        settings.MaxTalkCeilingMinutes.Should().Be(180);
        settings.MaxTalkMinutes.Should().Be(90);
        settings.PauseGraceSeconds.Should().Be(240);
        settings.IdleTimeoutSeconds.Should().Be(600);
    }

    [Fact]
    public async Task Missing_upstream_key_exits_cleanly_in_production()
    {
        var root = FindRepositoryRoot();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("PresenterAi.Api.dll");
        startInfo.ArgumentList.Add("--environment");
        startInfo.ArgumentList.Add("Production");
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add("http://127.0.0.1:0");
        startInfo.ArgumentList.Add("--Upstream:Endpoint=https://api.openai.com");
        startInfo.ArgumentList.Add("--Upstream:Key=");
        startInfo.ArgumentList.Add($"--Content:RootDir={root}");

        using var process = Process.Start(startInfo);
        process.Should().NotBeNull();

        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var waitTask = process.WaitForExitAsync();
        var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(60)));
        if (completed != waitTask)
        {
            process.Kill(entireProcessTree: true);
            await waitTask;
            throw new Xunit.Sdk.XunitException("API startup did not exit within 60 seconds.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var output = $"{stdout}\n{stderr}";

        process.ExitCode.Should().Be(1);
        stderr.Should().Contain("Missing required setting: Upstream:Key");
        output.Should().NotContain("   at ");
        output.Should().NotContain("Unhandled exception");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PresenterAi.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find PresenterAi.slnx from the test output directory.");
    }
}
