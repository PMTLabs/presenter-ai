using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using PresenterAi.Api.Errors;
using PresenterAi.Contracts;

namespace PresenterAi.Api.Middleware;

public static class AuthRateLimiting
{
    public static class Policies
    {
        public const string Authorize = "auth-authorize";
        public const string Callback = "auth-callback";
        public const string Token = "auth-token";
        public const string Refresh = "auth-refresh";
    }

    public sealed class Options { public bool Enabled { get; set; } = true; }

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<Options>(configuration.GetSection("RateLimiting"));
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, _) =>
            {
                var (limit, _) = LimitFor(context.HttpContext.Request.Path);
                context.HttpContext.Response.Headers["RateLimit-Limit"] = limit.ToString();
                context.HttpContext.Response.Headers["RateLimit-Remaining"] = "0";
                context.HttpContext.Response.Headers["RateLimit-Reset"] = "60";
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await Problems.Create(context.HttpContext, ErrorCodes.RateLimitExceeded,
                    StatusCodes.Status429TooManyRequests, "Too many requests. Please try again later.")
                    .ExecuteAsync(context.HttpContext).ConfigureAwait(false);
            };
            AddPolicy(options, Policies.Authorize, 20);
            AddPolicy(options, Policies.Callback, 20);
            AddPolicy(options, Policies.Token, 30);
            AddPolicy(options, Policies.Refresh, 30);
        });
        return services;
    }

    public static IApplicationBuilder UseAuthRateLimitHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var (limit, applies) = LimitFor(context.Request.Path);
            if (applies)
            {
                context.Response.Headers["RateLimit-Limit"] = limit.ToString();
                context.Response.Headers["RateLimit-Remaining"] = "1";
                context.Response.Headers["RateLimit-Reset"] = "60";
            }
            await next().ConfigureAwait(false);
        });

    private static void AddPolicy(RateLimiterOptions options, string name, int limit)
    {
        options.AddPolicy(name, context =>
        {
            var enabled = context.RequestServices.GetRequiredService<IOptions<Options>>().Value.Enabled;
            return enabled
                ? RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown-remote-ip",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = limit,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    })
                : RateLimitPartition.GetNoLimiter("disabled");
        });
    }

    private static (int Limit, bool Applies) LimitFor(PathString path)
    {
        var value = path.Value ?? string.Empty;
        if (value.EndsWith("/authorize", StringComparison.Ordinal)) return (20, true);
        if (value.EndsWith("/callback", StringComparison.Ordinal)) return (20, true);
        if (value.EndsWith("/sso/token", StringComparison.Ordinal)) return (30, true);
        if (value.EndsWith("/refresh", StringComparison.Ordinal)) return (30, true);
        return (0, false);
    }
}
