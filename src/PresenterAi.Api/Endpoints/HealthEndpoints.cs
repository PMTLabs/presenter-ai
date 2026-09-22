namespace PresenterAi.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", () => Results.Ok(new { status = "ok" }))
            .WithName("Health")
            .Produces(StatusCodes.Status200OK, typeof(object), "application/json");

        return endpoints;
    }
}
