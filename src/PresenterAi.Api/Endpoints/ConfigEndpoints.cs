using Microsoft.Extensions.Options;
using PresenterAi.Contracts.Presentations;
using PresenterAi.Infrastructure.Live;

namespace PresenterAi.Api.Endpoints;

public static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfigEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/config", (UpstreamRoutes routes, IOptions<PresenterOptions> presenter) =>
                Results.Ok(new ConfigDto(routes.Upstreams[0].Model, routes.Voice, presenter.Value.AdvanceSilenceMs)))
            .RequireAuthorization()
            .RequireCors("Default")
            .WithName("GetConfig")
            .Produces<ConfigDto>();
        return endpoints;
    }
}
