using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PresenterAi.Application.Auth;
using PresenterAi.Application.Content;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using PresenterAi.Application.Presenting;
using PresenterAi.Infrastructure.Content;
using PresenterAi.Infrastructure.Live;
using PresenterAi.Infrastructure.Persistence;
using PresenterAi.Infrastructure.Redis;

namespace PresenterAi.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres");
        services.AddDbContext<PresenterAiDbContext>(options =>
            options.UseNpgsql(connectionString ?? string.Empty, npgsql => npgsql.EnableRetryOnFailure()));
        services.AddScoped<IPresentationRepository, PostgresPresentationRepository>();
        return services;
    }

    public static IServiceCollection AddRedis(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Redis");
        // Validate the setting at API startup, but defer the network connection until the first auth-state
        // store is resolved. This keeps presentation-only test hosts bootable without Docker.
        services.AddSingleton<IConnectionMultiplexer>(_ => RedisConnection.Connect(connectionString ?? string.Empty));
        services.AddOptions<SessionRedisOptions>()
            .Bind(configuration.GetSection("Session"));
        services.AddSingleton<ITicketStore, TicketStore>();
        services.AddSingleton<ISsoCodeStore, SsoCodeStore>();
        services.AddSingleton<ISsoStateStore, SsoStateStore>();
        services.AddSingleton(serviceProvider => new Lazy<ISsoCodeStore>(
            () => serviceProvider.GetRequiredService<ISsoCodeStore>()));
        services.AddSingleton(serviceProvider => new Lazy<ISsoStateStore>(
            () => serviceProvider.GetRequiredService<ISsoStateStore>()));
        return services;
    }

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
        services.AddSingleton<IDeckStore>(serviceProvider =>
            new FileDeckStore(serviceProvider.GetRequiredService<string>()));
        return services;
    }

    public static IServiceCollection AddFileImportSource(this IServiceCollection services)
    {
        services.AddSingleton<IPresentationImportSource>(serviceProvider =>
            new FilePresentationRepository(serviceProvider.GetRequiredService<string>()));
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

    public static IServiceCollection AddPresenter(this IServiceCollection services, bool fileBacked = false)
    {
        services.AddSingleton<IPresenter>(serviceProvider =>
        {
            var factory = serviceProvider.GetRequiredService<ILiveSessionFactory>();
            var routes = serviceProvider.GetRequiredService<UpstreamRoutes>();
            var settings = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PresenterOptions>>().Value;
            var timeProvider = serviceProvider.GetRequiredService<TimeProvider>();

            Func<string, string, CancellationToken, Task<LoadedPresentation>> loader;
            if (fileBacked)
            {
                var source = serviceProvider.GetRequiredService<IPresentationImportSource>();
                loader = (_, id, cancellationToken) => source.ReadAsync(id, cancellationToken);
            }
            else
            {
                // The presenter is a singleton, so never capture the scoped DbContext or repository.
                // A fresh scope gives each load its own unit of work and preserves host scope validation.
                var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
                loader = async (ownerId, id, cancellationToken) =>
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var repository = scope.ServiceProvider.GetRequiredService<IPresentationRepository>();
                    return await repository.LoadAsync(ownerId, id, cancellationToken).ConfigureAwait(false);
                };
            }

            return new Presenter(
                (request, attempt) => attempt < routes.Upstreams.Count
                    ? factory.Create(routes.Upstreams[attempt], new LiveSessionConfig(routes.Upstreams[attempt].Model, request.Instructions, request.Voice))
                    : null,
                loader,
                new PresenterSettings(settings.AdvanceSilenceMs, routes.Voice),
                timeProvider);
        });
        return services;
    }
}
