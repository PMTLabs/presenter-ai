using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using PresenterAi.Api.Errors;
using PresenterAi.Contracts;

namespace PresenterAi.Api.Auth;

public sealed class DevAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Dev";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!configuration.GetValue<bool>("Auth:Dev:Enabled"))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new[]
        {
            new Claim("sub", configuration["Auth:Dev:UserId"] ?? "dev-user"),
            new Claim("email", configuration["Auth:Dev:Email"] ?? "dev@presenter-ai.local"),
            new Claim(ClaimTypes.Role, "user")
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = SchemeName;
        // Same producer as every other Problem Details response, so traceId/traceparent stay consistent.
        return Problems
            .Create(Context, ErrorCodes.AuthRequired, StatusCodes.Status401Unauthorized,
                "Authentication is required to access this resource.")
            .ExecuteAsync(Context);
    }
}
