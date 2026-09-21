using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PresenterAi.Infrastructure.Live;

namespace PresenterAi.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddUpstreamOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<UpstreamOptions>()
            .Bind(configuration.GetSection("Upstream"))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Endpoint),
                "Missing required setting: Upstream:Endpoint")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Key),
                "Missing required setting: Upstream:Key")
            .ValidateOnStart();

        services.AddOptions<PresenterOptions>()
            .Bind(configuration.GetSection("Presenter"));

        services.AddSingleton<UpstreamRoutes>(serviceProvider =>
            UpstreamRoutes.From(serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<UpstreamOptions>>().Value));

        return services;
    }

    public static IServiceCollection AddLiveSessions(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<LiveSessionOptions>();
        services.AddSingleton<ILiveSessionFactory, LiveSessionFactory>();
        return services;
    }
}
