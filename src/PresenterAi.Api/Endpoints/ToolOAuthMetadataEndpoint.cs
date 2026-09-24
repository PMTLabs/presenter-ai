using Microsoft.Extensions.Options;
using PresenterAi.Api.Auth;
using PresenterAi.Infrastructure.Tools;

namespace PresenterAi.Api.Endpoints;

public static class ToolOAuthMetadataEndpoint
{
    public static void MapToolOAuthMetadataEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/tools/oauth/client-metadata.json", (IOptions<OAuthSettings> oauth,
            IOptions<ExternalToolsOptions> tools) =>
        {
            var baseUrl = oauth.Value.ApiBaseUrl;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                return Results.NotFound();
            var redirect = tools.Value.OAuthRedirectUri ??
                new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "tools/oauth/callback").AbsoluteUri;
            return Results.Json(new
            {
                client_id = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "v1/tools/oauth/client-metadata.json").AbsoluteUri,
                client_name = "Presenter AI",
                redirect_uris = new[] { redirect },
                grant_types = new[] { "authorization_code", "refresh_token" },
                response_types = new[] { "code" },
                token_endpoint_auth_method = "none"
            });
        }).AllowAnonymous();
    }
}
