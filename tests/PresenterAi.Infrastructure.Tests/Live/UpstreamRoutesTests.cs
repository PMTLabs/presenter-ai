using Microsoft.Extensions.Configuration;
using PresenterAi.Infrastructure.Live;

namespace PresenterAi.Infrastructure.Tests.Live;

public sealed class UpstreamRoutesTests
{
    [Fact]
    public void Missing_setting_names_the_key()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["Upstream:Endpoint"] = "https://api.openai.com",
            ["Upstream:Key"] = "  "
        });

        var exception = Assert.Throws<InvalidOperationException>(() => UpstreamRoutes.From(options));
        Assert.Equal("Missing required setting: Upstream:Key", exception.Message);
    }

    [Fact]
    public void Defaults_are_applied_and_strings_are_trimmed()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["Upstream:Endpoint"] = " https://api.openai.com ",
            ["Upstream:Key"] = " k ",
            ["Upstream:Voice"] = " marin "
        });

        var routes = UpstreamRoutes.From(options);

        Assert.Equal("wss://api.openai.com/v1/live/sessions", routes.Upstreams[0].LiveUrl.ToString());
        Assert.Equal("gpt-live-1", routes.Upstreams[0].Model);
        Assert.Equal("gpt-5.6-luna", routes.Upstreams[0].DelegationModel);
        Assert.Equal("marin", routes.Voice);
        Assert.Equal("Bearer k", routes.Upstreams[0].Headers["Authorization"]);
    }

    [Fact]
    public void Presenter_options_bind_defaults_and_values()
    {
        var defaultConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var defaults = new PresenterOptions();
        defaultConfig.GetSection("Presenter").Bind(defaults);
        Assert.Equal(3000, defaults.AdvanceSilenceMs);
        Assert.Equal(5000, defaults.FollowUpWaitMs);
        Assert.False(defaults.LogEvents);

        var configured = new PresenterOptions();
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Presenter:AdvanceSilenceMs"] = "1500",
                ["Presenter:FollowUpWaitMs"] = "9000",
                ["Presenter:LogEvents"] = "true"
            })
            .Build()
            .GetSection("Presenter")
            .Bind(configured);
        Assert.Equal(1500, configured.AdvanceSilenceMs);
        Assert.Equal(9000, configured.FollowUpWaitMs);
        Assert.True(configured.LogEvents);
    }

    [Fact]
    public void Empty_delegation_models_select_client_delegation()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["Upstream:Endpoint"] = "https://api.openai.com",
            ["Upstream:Key"] = "k",
            ["Upstream:DelegationModel"] = "   ",
            ["Upstream:Fallback:Key"] = "fallback",
            ["Upstream:Fallback:DelegationModel"] = "   "
        });

        var routes = UpstreamRoutes.From(options);

        Assert.Equal(string.Empty, routes.Upstreams[0].DelegationModel);
        Assert.Equal(string.Empty, routes.Upstreams[1].DelegationModel);
    }

    [Fact]
    public void Fallback_key_adds_second_route_after_primary()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["Upstream:Endpoint"] = "https://x.services.ai.azure.com",
            ["Upstream:Key"] = "k",
            ["Upstream:Model"] = "dep",
            ["Upstream:Fallback:Key"] = " sk-fb ",
            ["Upstream:Fallback:Model"] = " openai/gpt-live-1 "
        });

        var routes = UpstreamRoutes.From(options);

        Assert.Equal(2, routes.Upstreams.Count);
        Assert.Equal("primary", routes.Upstreams[0].Name);
        Assert.Equal("fallback", routes.Upstreams[1].Name);
        Assert.Equal("dep", routes.Upstreams[0].Model);
        Assert.Equal("openai/gpt-live-1", routes.Upstreams[1].Model);
        Assert.Equal("wss://api.openai.com/v1/live/sessions", routes.Upstreams[1].LiveUrl.ToString());
        Assert.Equal("Bearer k", routes.Upstreams[0].Headers["Authorization"]);
        Assert.Equal("k", routes.Upstreams[0].Headers["api-key"]);
        Assert.Equal("Bearer sk-fb", routes.Upstreams[1].Headers["Authorization"]);
    }

    [Fact]
    public void Custom_fallback_endpoint_preserves_route_order_models_and_headers()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["Upstream:Endpoint"] = "https://api.openai.com",
            ["Upstream:Key"] = "k",
            ["Upstream:Model"] = "primary-model",
            ["Upstream:Fallback:Key"] = " sk-fb ",
            ["Upstream:Fallback:Endpoint"] = "https://gw.example.com",
            ["Upstream:Fallback:Model"] = " openai/gpt-live-1 "
        });

        var routes = UpstreamRoutes.From(options);

        Assert.Equal(2, routes.Upstreams.Count);
        Assert.Equal("primary", routes.Upstreams[0].Name);
        Assert.Equal("fallback", routes.Upstreams[1].Name);
        Assert.Equal("wss://api.openai.com/v1/live/sessions", routes.Upstreams[0].LiveUrl.ToString());
        Assert.Equal("wss://gw.example.com/v1/live/sessions", routes.Upstreams[1].LiveUrl.ToString());
        Assert.Equal("primary-model", routes.Upstreams[0].Model);
        Assert.Equal("openai/gpt-live-1", routes.Upstreams[1].Model);
        Assert.Equal("Bearer k", routes.Upstreams[0].Headers["Authorization"]);
        Assert.Equal("Bearer sk-fb", routes.Upstreams[1].Headers["Authorization"]);
    }

    private static UpstreamOptions Bind(IReadOnlyDictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var options = new UpstreamOptions();
        configuration.GetSection("Upstream").Bind(options);
        return options;
    }
}
