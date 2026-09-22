using Microsoft.EntityFrameworkCore;
using PresenterAi.Infrastructure.Persistence;
using StackExchange.Redis;

namespace PresenterAi.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", async (HttpContext context) =>
            {
                // Test hosts intentionally do not need either stateful service. Production and
                // staging get a shallow reachability probe here instead of a registration-time
                // network dial; this is the operator-facing fail-fast surface.
                if (context.RequestServices.GetRequiredService<IHostEnvironment>().IsEnvironment("Testing"))
                    return Results.Ok(new { status = "ok" });

                var postgres = false;
                var redis = false;
                try
                {
                    var db = context.RequestServices.GetRequiredService<PresenterAiDbContext>();
                    postgres = await db.Database.CanConnectAsync(context.RequestAborted);
                }
                catch { }

                try
                {
                    var connection = context.RequestServices.GetRequiredService<IConnectionMultiplexer>();
                    await connection.GetDatabase().PingAsync();
                    redis = true;
                }
                catch { }

                var healthy = postgres && redis;
                return Results.Json(new
                {
                    status = healthy ? "ok" : "degraded",
                    postgres = postgres ? "ok" : "unreachable",
                    redis = redis ? "ok" : "unreachable"
                }, statusCode: healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
            })
            .AllowAnonymous()
            .WithName("Health")
            .Produces(StatusCodes.Status200OK, typeof(object), "application/json")
            .Produces(StatusCodes.Status503ServiceUnavailable, typeof(object), "application/json");

        return endpoints;
    }
}
