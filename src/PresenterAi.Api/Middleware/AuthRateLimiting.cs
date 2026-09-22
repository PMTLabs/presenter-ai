using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using PresenterAi.Api.Errors;
using PresenterAi.Contracts;

namespace PresenterAi.Api.Middleware;

public static class AuthRateLimiting
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

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
                var (_, limit) = PolicyFor(context.HttpContext.Request.Path);
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan value)
                    ? value
                    : Window;
                var reset = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
                context.HttpContext.Response.Headers["RateLimit-Limit"] = limit.ToString();
                context.HttpContext.Response.Headers["RateLimit-Remaining"] = "0";
                context.HttpContext.Response.Headers["RateLimit-Reset"] = reset.ToString();
                context.HttpContext.Response.Headers.RetryAfter = reset.ToString();
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
            if (context.RequestServices.GetRequiredService<IOptions<Options>>().Value.Enabled
                && PolicyFor(context.Request.Path) is (_, var limit) && limit > 0)
            {
                context.Response.Headers["RateLimit-Limit"] = limit.ToString();
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
                        Window = Window,
                        QueueLimit = 0
                    })
                : RateLimitPartition.GetNoLimiter("disabled");
        });
    }

    private static (string? Policy, int Limit) PolicyFor(PathString path)
    {
        var value = path.Value ?? string.Empty;
        if (value.EndsWith("/authorize", StringComparison.Ordinal)) return (Policies.Authorize, 20);
        if (value.EndsWith("/callback", StringComparison.Ordinal)) return (Policies.Callback, 20);
        if (value.EndsWith("/sso/token", StringComparison.Ordinal)) return (Policies.Token, 30);
        if (value.EndsWith("/refresh", StringComparison.Ordinal)) return (Policies.Refresh, 30);
        return (null, 0);
    }
}
