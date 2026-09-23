using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PresenterAi.Application.Tools;
using PresenterAi.Infrastructure;
using Xunit;

namespace PresenterAi.Infrastructure.Tests.Tools;

public sealed class ToolsOptionsTests
{
    [Fact]
    public void Default_MaxInlineTools_is_16()
    {
        var options = new ToolsOptions();
        options.MaxInlineTools.Should().Be(16);
    }

    [Fact]
    public void Config_binds_MaxInlineTools()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Upstream:Endpoint"] = "https://example.com",
                ["Upstream:Key"] = "test-key",
                ["Tools:MaxInlineTools"] = "32"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddUpstreamOptions(config);

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<ToolsOptions>>().Value;

        options.MaxInlineTools.Should().Be(32);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("129")]
    public void Invalid_MaxInlineTools_fails_validation(string invalidValue)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Upstream:Endpoint"] = "https://example.com",
                ["Upstream:Key"] = "test-key",
                ["Tools:MaxInlineTools"] = invalidValue
            })
            .Build();

        var services = new ServiceCollection();
        services.AddUpstreamOptions(config);

        var sp = services.BuildServiceProvider();
        var act = () => sp.GetRequiredService<IOptions<ToolsOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Tools:MaxInlineTools must be between*");
    }
}
