using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PresenterAi.Application.Auth;
using PresenterAi.Application.Content;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using PresenterAi.Application.Presenting;
using PresenterAi.Application.Scripts.Revisions;
using PresenterAi.Application.Sessions;
using PresenterAi.Application.Tools;
using PresenterAi.Application.Tools.External;
using PresenterAi.Infrastructure.Tools;
using PresenterAi.Infrastructure.Content;
using PresenterAi.Infrastructure.Live;
using PresenterAi.Infrastructure.Persistence;
using PresenterAi.Infrastructure.Redis;
using PresenterAi.Infrastructure.Sessions;
using PresenterAi.Infrastructure.Training;

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
        // Plan 010: scoped like the DbContext; singletons (the revision service) open a scope per store operation.
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IPresentationRevisionStore, PostgresPresentationRevisionStore>();
        services.AddScoped<IToolConnectionRepository, PostgresToolConnectionRepository>();
        services.TryAddSingleton<ISessionRecorderFactory, SessionRecorderFactory>();
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
            .Bind(configuration.GetSection("Session"))
            .Validate(
                options => options.HeartbeatIntervalSeconds is >= SessionRedisOptions.MinHeartbeatIntervalSeconds
                    and <= SessionRedisOptions.MaxHeartbeatIntervalSeconds,
                $"Session:HeartbeatIntervalSeconds must be between {SessionRedisOptions.MinHeartbeatIntervalSeconds} and {SessionRedisOptions.MaxHeartbeatIntervalSeconds}")
            .Validate(
                options => options.HeartbeatTimeoutSeconds >= 2 * options.HeartbeatIntervalSeconds
                    && options.HeartbeatTimeoutSeconds <= SessionRedisOptions.MaxHeartbeatTimeoutSeconds,
                $"Session:HeartbeatTimeoutSeconds must be between 2 * HeartbeatIntervalSeconds and {SessionRedisOptions.MaxHeartbeatTimeoutSeconds}")
            .ValidateOnStart();
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
            .Bind(configuration.GetSection("Presenter"))
            // Below ~2.5 s the resume could fire inside a pause of the answer itself.
            .Validate(
                options => options.FollowUpWaitMs is >= PresenterOptions.MinFollowUpWaitMs and <= PresenterOptions.MaxFollowUpWaitMs,
                $"Presenter:FollowUpWaitMs must be between {PresenterOptions.MinFollowUpWaitMs} and {PresenterOptions.MaxFollowUpWaitMs}")
            .Validate(
                options => options.MaxTalkCeilingMinutes is >= PresenterOptions.MinMaxTalkCeilingMinutes
                    and <= PresenterOptions.MaxMaxTalkCeilingMinutes,
                $"Presenter:MaxTalkCeilingMinutes must be between {PresenterOptions.MinMaxTalkCeilingMinutes} and {PresenterOptions.MaxMaxTalkCeilingMinutes}")
            .Validate(
                options => options.MaxTalkMinutes >= PresenterOptions.MinMaxTalkMinutes
                    && options.MaxTalkMinutes <= options.MaxTalkCeilingMinutes,
                $"Presenter:MaxTalkMinutes must be between {PresenterOptions.MinMaxTalkMinutes} and Presenter:MaxTalkCeilingMinutes")
            .Validate(
                options => options.PauseGraceSeconds is >= PresenterOptions.MinPauseGraceSeconds
                    and <= PresenterOptions.MaxPauseGraceSeconds,
                $"Presenter:PauseGraceSeconds must be between {PresenterOptions.MinPauseGraceSeconds} and {PresenterOptions.MaxPauseGraceSeconds}")
            .Validate(
                options => options.IdleTimeoutSeconds is >= PresenterOptions.MinIdleTimeoutSeconds
                    and <= PresenterOptions.MaxIdleTimeoutSeconds,
                $"Presenter:IdleTimeoutSeconds must be between {PresenterOptions.MinIdleTimeoutSeconds} and {PresenterOptions.MaxIdleTimeoutSeconds}")
            .ValidateOnStart();

        services.AddOptions<ToolsOptions>()
            .Bind(configuration.GetSection("Tools"))
            .Validate(
                options => options.MaxInlineTools is >= ToolsOptions.MinMaxInlineTools and <= ToolsOptions.MaxMaxInlineTools,
                $"Tools:MaxInlineTools must be between {ToolsOptions.MinMaxInlineTools} and {ToolsOptions.MaxMaxInlineTools}")
            .ValidateOnStart();

        services.AddOptions<ExternalToolsOptions>()
            .Bind(configuration.GetSection("Tools"))
            .Validate(options => ExternalToolsOptions.ValidKey(options.CredentialKey), "Tools:CredentialKey must decode to 32 bytes")
            .Validate(options => ExternalToolsOptions.ValidRedirect(options.OAuthRedirectUri), "Tools:OAuthRedirectUri must be absolute")
            .Validate(options => options.Mcp.StartBudgetMs > 0 && options.Mcp.CallTimeoutSeconds > 0,
                "Tools:Mcp budgets must be positive")
            .ValidateOnStart();

        // Plan 010: bounds each script reviser call; read by ScriptRevisionService.
        services.AddOptions<TrainingOptions>()
            .Bind(configuration.GetSection(TrainingOptions.SectionName))
            .Validate(
                options => options.ReviserTimeoutSeconds is >= TrainingOptions.MinReviserTimeoutSeconds
                    and <= TrainingOptions.MaxReviserTimeoutSeconds,
                $"Training:ReviserTimeoutSeconds must be between {TrainingOptions.MinReviserTimeoutSeconds} and {TrainingOptions.MaxReviserTimeoutSeconds}")
            .ValidateOnStart();
        services.AddSingleton<CredentialProtector>();

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
        services.TryAddSingleton<ToolRegistry>();
        AddScriptTraining(services);

        if (fileBacked)
        {
            // Plan 010: file mode has no database, so versions live in memory for the process, seeded from the file.
            services.AddSingleton<IPresentationRevisionStore>(serviceProvider =>
            {
                var source = serviceProvider.GetRequiredService<IPresentationImportSource>();
                return new InMemoryPresentationRevisionStore(
                    async (_, id, cancellationToken) =>
                    {
                        try
                        {
                            return (await source.ReadSourceAsync(id, cancellationToken).ConfigureAwait(false)).Markdown;
                        }
                        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or ArgumentException)
                        {
                            return null;
                        }
                    },
                    serviceProvider.GetService<TimeProvider>());
            });
        }

        services.AddSingleton<IPresenter>(serviceProvider =>
        {
            var factory = serviceProvider.GetRequiredService<ILiveSessionFactory>();
            var routes = serviceProvider.GetRequiredService<UpstreamRoutes>();
            var settings = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PresenterOptions>>().Value;
            var timeProvider = serviceProvider.GetRequiredService<TimeProvider>();
            var toolsOptions = serviceProvider.GetService<Microsoft.Extensions.Options.IOptions<ToolsOptions>>()?.Value;
            var toolRegistry = serviceProvider.GetService<ToolRegistry>();

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
                    ? factory.Create(routes.Upstreams[attempt], new LiveSessionConfig(
                        routes.Upstreams[attempt].Model,
                        request.Instructions,
                        request.Voice,
                        request.Title,
                        request.Tools,
                        request.DelegationInstructions,
                        request.HostedTools))
                    : null,
                loader,
                new PresenterSettings(
                    settings.AdvanceSilenceMs,
                    routes.Voice,
                    settings.FollowUpWaitMs,
                    toolsOptions?.MaxInlineTools ?? ToolsOptions.DefaultMaxInlineTools,
                    settings.MaxTalkMinutes,
                    settings.MaxTalkCeilingMinutes,
                    settings.PauseGraceSeconds,
                    settings.IdleTimeoutSeconds),
                timeProvider,
                toolRegistry,
                attempt => attempt < routes.Upstreams.Count && !string.IsNullOrWhiteSpace(routes.Upstreams[attempt].DelegationModel),
                routes.Upstreams.Any(route => !string.IsNullOrWhiteSpace(route.DelegationModel))
                    && serviceProvider.GetRequiredService<IServiceProviderIsService>().IsService(typeof(ISessionToolSource))
                    ? async (ownerId, ct) =>
                    {
                        await using var scope = serviceProvider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
                        return await scope.ServiceProvider.GetRequiredService<ISessionToolSource>().LoadAsync(ownerId, ct).ConfigureAwait(false);
                    } : null,
                TimeSpan.FromMilliseconds((serviceProvider.GetService<Microsoft.Extensions.Options.IOptions<ExternalToolsOptions>>()?.Value.Mcp.StartBudgetMs ?? 3000) + 1000),
                // Plan 010: optional so hosts without the revision service still build; the presenter subscribes to its
                // Changed signal itself (a reconcile request only).
                serviceProvider.GetService<IScriptRevisionService>());
        });
        return services;
    }

    // Plan 010 T5/T6: the out-of-band Responses reviser on the upstream routes and the revision service. The named client has no timeout of its
    // own (Training:ReviserTimeoutSeconds and the caller's token bound each call) and no logging handlers, so request
    // URLs and headers never reach the logs.
    private static void AddScriptTraining(IServiceCollection services)
    {
        services.AddHttpClient(ResponsesScriptReviser.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IScriptReviser>(serviceProvider => new ResponsesScriptReviser(
            serviceProvider.GetRequiredService<IHttpClientFactory>(),
            serviceProvider.GetRequiredService<UpstreamRoutes>(),
            serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<TrainingOptions>>().Value.ReviserTimeout,
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetService<Microsoft.Extensions.Logging.ILogger<ResponsesScriptReviser>>()));

        // Plan 010 T6: singleton; it resolves IPresentationRevisionStore from a fresh async scope per store operation.
        services.TryAddSingleton<IScriptRevisionService>(serviceProvider => new ScriptRevisionService(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            serviceProvider.GetRequiredService<IScriptReviser>(),
            serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<TrainingOptions>>().Value,
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetService<Microsoft.Extensions.Logging.ILogger<ScriptRevisionService>>()));
    }
}
