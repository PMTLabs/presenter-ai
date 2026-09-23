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
