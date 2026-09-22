using Microsoft.Extensions.Options;
using PresenterAi.Contracts.Presentations;
using PresenterAi.Infrastructure.Live;

namespace PresenterAi.Api.Endpoints;

public static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfigEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/config", (UpstreamRoutes routes, IOptions<PresenterOptions> presenter) =>
                Results.Ok(new ConfigDto(routes.Upstreams[0].Model, routes.Voice, presenter.Value.AdvanceSilenceMs)))
            .RequireAuthorization()
            .WithName("GetConfig")
            .Produces<ConfigDto>();
        return endpoints;
    }
}
