using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
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
        public const string Tools = "tools";
    }

    public sealed class Options { public bool Enabled { get; set; } = true; }

    private static readonly IReadOnlyDictionary<string, int> Limits =
        new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [Policies.Authorize] = 20,
        [Policies.Callback] = 20,
        [Policies.Token] = 30,
        [Policies.Refresh] = 30,
        [Policies.Tools] = 30
    };

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<Options>(configuration.GetSection("RateLimiting"));
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, _) =>
            {
                if (LimitFor(context.HttpContext) is { } limit)
                {
                    var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan value)
                        ? value
                        : Window;
                    var reset = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
                    context.HttpContext.Response.Headers["RateLimit-Limit"] = limit.ToString();
                    context.HttpContext.Response.Headers["RateLimit-Remaining"] = "0";
                    context.HttpContext.Response.Headers["RateLimit-Reset"] = reset.ToString();
                    context.HttpContext.Response.Headers.RetryAfter = reset.ToString();
                }
                await Problems.Create(context.HttpContext, ErrorCodes.RateLimitExceeded,
                    StatusCodes.Status429TooManyRequests, "Too many requests. Please try again later.")
                    .ExecuteAsync(context.HttpContext).ConfigureAwait(false);
            };
            foreach (var (name, limit) in Limits)
                AddPolicy(options, name, limit);
        });
        return services;
    }

    public static IApplicationBuilder UseAuthRateLimitHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.RequestServices.GetRequiredService<IOptions<Options>>().Value.Enabled
                && LimitFor(context) is { } limit)
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
            if (!enabled) return RateLimitPartition.GetNoLimiter("disabled");

            var key = name == Policies.Tools
                ? (context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? context.User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                   ?? context.Connection.RemoteIpAddress?.ToString()
                   ?? "unknown-user")
                : (context.Connection.RemoteIpAddress?.ToString() ?? "unknown-remote-ip");

            return RateLimitPartition.GetFixedWindowLimiter(
                key,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limit,
                    Window = Window,
                    QueueLimit = 0
                });
        });
    }

    private static int? LimitFor(HttpContext context)
    {
        var policy = context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        return policy is not null && Limits.TryGetValue(policy, out var limit) ? limit : null;
    }
}
