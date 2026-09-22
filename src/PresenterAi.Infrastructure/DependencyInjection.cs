using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PresenterAi.Application.Content;
using PresenterAi.Application.Presenting;
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
        // Presenter:LogEvents (Node LOG_EVENTS) is the only reader-facing switch; it feeds the session's event logging.
        services.TryAddSingleton(serviceProvider => new LiveSessionOptions
        {
            LogEvents = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PresenterOptions>>().Value.LogEvents
        });
        services.AddSingleton<ILiveSessionFactory, LiveSessionFactory>();
        return services;
    }

    public static IServiceCollection AddPresenter(this IServiceCollection services)
    {
        services.AddSingleton<IPresenter>(serviceProvider =>
        {
            var repository = serviceProvider.GetRequiredService<IPresentationRepository>();
            var factory = serviceProvider.GetRequiredService<ILiveSessionFactory>();
            var routes = serviceProvider.GetRequiredService<UpstreamRoutes>();
            var settings = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PresenterOptions>>().Value;
            var timeProvider = serviceProvider.GetRequiredService<TimeProvider>();

            return new Presenter(
                (request, attempt) => attempt < routes.Upstreams.Count
                    ? factory.Create(routes.Upstreams[attempt], new LiveSessionConfig(routes.Upstreams[attempt].Model, request.Instructions, request.Voice))
                    : null,
                repository.LoadAsync,
                new PresenterSettings(settings.AdvanceSilenceMs, routes.Voice),
                timeProvider);
        });
        return services;
    }
}
