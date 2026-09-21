using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PresenterAi.Application.Content;
using PresenterAi.Infrastructure.Content;
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

    public static IServiceCollection AddFileContent(
        this IServiceCollection services,
        IConfiguration configuration,
        string contentRootPath)
    {
        services.AddOptions<ContentOptions>()
            .Bind(configuration.GetSection("Content"));
        services.AddSingleton(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ContentOptions>>().Value;
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootDir, contentRootPath));
        });
        services.AddSingleton<IPresentationRepository>(serviceProvider =>
            new FilePresentationRepository(serviceProvider.GetRequiredService<string>()));
        services.AddSingleton<IDeckStore>(serviceProvider =>
            new FileDeckStore(serviceProvider.GetRequiredService<string>()));
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
