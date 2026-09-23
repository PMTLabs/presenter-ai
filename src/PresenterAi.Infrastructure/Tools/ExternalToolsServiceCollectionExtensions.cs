using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PresenterAi.Infrastructure.Tools.Mcp;

namespace PresenterAi.Infrastructure.Tools;

public static class ExternalToolsServiceCollectionExtensions
{
    // Called by the API only; CLI outbound transports are deliberately unaffected.
    public static IServiceCollection AddExternalTools(this IServiceCollection services)
    {
        services.AddSingleton<McpOAuthStateStore>();
        services.AddScoped<McpOAuthService>();
        services.TryAddSingleton<IOutboundAddressPolicy, StrictOutboundAddressPolicy>();
        services.TryAddSingleton<IOutboundDnsResolver, OutboundDnsResolver>();
        services.TryAddSingleton<ISocketConnector, SocketConnector>();
        services.TryAddSingleton<GuardedConnectCallback>();
        services.TryAddSingleton(sp => new SocketsHttpHandler
        {
            ConnectCallback = sp.GetRequiredService<GuardedConnectCallback>().ConnectAsync,
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        });
        services.AddHttpClient("mcp")
            .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<SocketsHttpHandler>())
            .AddHttpMessageHandler(sp => new OutboundRequestHandler(sp.GetRequiredService<IOutboundAddressPolicy>(), 1024 * 1024))
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers();
        services.AddHttpClient("mcp-oauth")
            .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<SocketsHttpHandler>())
            .AddHttpMessageHandler(sp => new OutboundRequestHandler(sp.GetRequiredService<IOutboundAddressPolicy>(), 64 * 1024))
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers();
        return services;
    }
}
