using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PresenterAi.Api.Auth;
using PresenterAi.Api.Errors;
using PresenterAi.Api.Middleware;
using PresenterAi.Contracts;
using PresenterAi.Contracts.Auth;
using PresenterAi.Infrastructure.Identity;

namespace PresenterAi.Api.Endpoints;

public static class AuthEndpoints
{
    private const string CallbackFailureDetail = "Sign-in could not be completed. Please try again.";

    public static IEndpointRouteBuilder MapAuthEndpoints(
        this IEndpointRouteBuilder endpoints,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var group = endpoints.MapGroup("/v1/auth").WithTags("Auth").RequireCors("Default");
        var sso = group.MapGroup("/sso");

        sso.MapGet("/providers", (SsoService service) =>
                Results.Ok(new ListResponse<SsoProviderResponse>(
                    service.GetEnabledProviders().Select(id => new SsoProviderResponse(id)).ToArray(),
                    1,
                    25,
                    service.GetEnabledProviders().Count)))
            .AllowAnonymous()
            .WithName("ListSsoProviders")
            .Produces<ListResponse<SsoProviderResponse>>();

        sso.MapGet("/{provider}/authorize", (
                [FromRoute] string provider,
                [FromQuery(Name = "redirect_uri")] string redirectUri,
                [FromQuery(Name = "code_challenge")] string codeChallenge,
                [FromQuery] string state,
                [FromQuery(Name = "client_hint")] string? clientHint,
                SsoService service,
                HttpContext context) =>
            Execute(() => Results.Redirect(service.BuildAuthorizationUrl(provider, redirectUri, codeChallenge, state, clientHint)), context))
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimiting.Policies.Authorize)
            .WithName("AuthorizeSso")
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        sso.MapGet("/{provider}/callback", async (
                [FromRoute] string provider,
                [FromQuery] string code,
                [FromQuery] string state,
                SsoService service,
                HttpContext context,
                ILogger<SsoService> logger) =>
            {
                try
                {
                    var redirect = await service.HandleCallbackAsync(provider, code, state, context.RequestAborted);
                    return Results.Redirect(redirect);
                }
                catch (SsoFailureException exception)
                {
                    logger.LogWarning(exception, "SSO callback failed ({Code}) trace {TraceId}", exception.Code, context.TraceIdentifier);
                    return Problems.Create(context, exception.Code, exception.Status, CallbackFailureDetail);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "SSO callback failed for provider {Provider}, trace {TraceId}", provider, context.TraceIdentifier);
                    return Problems.Create(context, ErrorCodes.AuthSsoStateInvalid, StatusCodes.Status400BadRequest, CallbackFailureDetail);
                }
            })
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimiting.Policies.Callback)
            .WithName("SsoCallback")
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        sso.MapPost("/token", async (
                SsoTokenRequest request,
                SsoService service,
                TokenService tokens,
                HttpContext context,
                IOptions<JwtSettings> jwtSettings,
                TimeProvider timeProvider) =>
            {
                try
                {
                    var user = await service.RedeemCodeAsync(request.Code, request.CodeVerifier, context.RequestAborted);
                    var issued = await tokens.IssueAsync(user, context.RequestAborted);
                    RefreshCookie.Append(context.Response, issued.RefreshToken,
                        timeProvider.GetUtcNow().AddDays(jwtSettings.Value.RefreshTokenDays));
                    return Results.Ok(TokenService.ToResponse(issued));
                }
                catch (SsoFailureException exception)
                {
                    return Problems.Create(context, exception.Code, exception.Status, exception.Detail);
                }
                catch (AuthFailureException exception)
                {
                    return Problems.Create(context, exception.Code, exception.Status, exception.Detail);
                }
            })
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimiting.Policies.Token)
            .WithName("ExchangeSsoToken")
            .Produces<AuthTokenResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/refresh", async (
                TokenService tokens,
                HttpContext context,
                IOptions<JwtSettings> jwtSettings,
                TimeProvider timeProvider) =>
            {
                if (!OriginGuard.IsAllowed(context.Request, configuration))
                    return Problems.Create(context, ErrorCodes.AuthForbidden, StatusCodes.Status403Forbidden,
                        "The request origin is not allowed.");
                if (!context.Request.Cookies.TryGetValue(RefreshCookie.Name, out var refreshToken)
                    || string.IsNullOrWhiteSpace(refreshToken))
                    return Problems.Create(context, ErrorCodes.AuthRequired, StatusCodes.Status401Unauthorized,
                        "Authentication is required.");
                try
                {
                    var issued = await tokens.RotateAsync(refreshToken, context.RequestAborted);
                    RefreshCookie.Append(context.Response, issued.RefreshToken,
                        timeProvider.GetUtcNow().AddDays(jwtSettings.Value.RefreshTokenDays));
                    return Results.Ok(TokenService.ToResponse(issued));
                }
                catch (AuthFailureException exception)
                {
                    return Problems.Create(context, exception.Code, exception.Status, exception.Detail);
                }
            })
            .AllowAnonymous()
            .RequireCors("Refresh")
            .RequireRateLimiting(AuthRateLimiting.Policies.Refresh)
            .WithName("RefreshAuth")
            .Produces<AuthTokenResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/logout", async (TokenService tokens, HttpContext context) =>
            {
                if (!OriginGuard.IsAllowed(context.Request, configuration))
                    return Problems.Create(context, ErrorCodes.AuthForbidden, StatusCodes.Status403Forbidden,
                        "The request origin is not allowed.");
                if (context.Request.Cookies.TryGetValue(RefreshCookie.Name, out var refreshToken)
                    && !string.IsNullOrWhiteSpace(refreshToken))
                    await tokens.LogoutAsync(refreshToken, context.RequestAborted);
                RefreshCookie.Delete(context.Response);
                return Results.NoContent();
            })
            .AllowAnonymous()
            .RequireCors("Refresh")
            .WithName("LogoutAuth")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/me", async (ClaimsPrincipal principal, TokenService tokens, HttpContext context) =>
            {
                try
                {
                    var user = await tokens.GetUserAsync(principal, context.RequestAborted);
                    return Results.Ok(TokenService.ToUserResponse(user));
                }
                catch (AuthFailureException exception)
                {
                    return Problems.Create(context, exception.Code, exception.Status, exception.Detail);
                }
            })
            .RequireAuthorization()
            .WithName("GetCurrentUser")
            .Produces<AuthUserResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        if (configuration.GetValue<bool>("Auth:Dev:Enabled") && environment.IsDevelopment())
        {
            group.MapPost("/dev/sign-in", async (
                    TokenService tokens,
                    IConfiguration config,
                    HttpContext context,
                    TimeProvider timeProvider) =>
                {
                    try
                    {
                        var user = await tokens.UpsertDevelopmentUserAsync(config, context.RequestAborted);
                    var issued = await tokens.IssueAsync(user, context.RequestAborted);
                    RefreshCookie.Append(context.Response, issued.RefreshToken,
                        timeProvider.GetUtcNow().AddDays(config.GetValue<int>("Jwt:RefreshTokenDays", 30)));
                    return Results.Ok(TokenService.ToResponse(issued));
                }
                catch (AuthFailureException exception)
                {
                    return Problems.Create(context, exception.Code, exception.Status, exception.Detail);
                }
                })
                .AllowAnonymous()
                .WithName("DevSignIn")
                .Produces<AuthTokenResponse>()
                .ProducesProblem(StatusCodes.Status403Forbidden);
        }

        return endpoints;
    }

    private static IResult Execute(Func<IResult> action, HttpContext context)
    {
        try
        {
            return action();
        }
        catch (SsoFailureException exception)
        {
            return Problems.Create(context, exception.Code, exception.Status, exception.Detail);
        }
    }
}
