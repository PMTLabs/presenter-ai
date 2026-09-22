using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using PresenterAi.Api.Auth;
using PresenterAi.Api.Errors;
using PresenterAi.Application.Auth;
using PresenterAi.Contracts.Auth;
using PresenterAi.Infrastructure.Redis;

namespace PresenterAi.Api.Endpoints;

public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/sessions/ticket", async (
                ClaimsPrincipal principal,
                TokenService tokens,
                ITicketStore ticketStore,
                IOptions<SessionRedisOptions> sessionOptions,
                HttpContext context) =>
            {
                try
                {
                    // GetUserAsync deliberately loads the row again so a disabled account cannot
                    // turn an already-issued access token into a new live session.
                    var user = await tokens.GetUserAsync(principal, context.RequestAborted).ConfigureAwait(false);
                    var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                        .Replace("+", "-", StringComparison.Ordinal)
                        .Replace("/", "_", StringComparison.Ordinal)
                        .TrimEnd('=');
                    await ticketStore.IssueAsync(ticket, user.Id, context.RequestAborted).ConfigureAwait(false);
                    return Results.Ok(new SessionTicketResponse(ticket, sessionOptions.Value.TicketTtlSeconds));
                }
                catch (AuthFailureException exception)
                {
                    return Problems.Create(context, exception.Code, exception.Status, exception.Detail);
                }
            })
            .RequireAuthorization()
            .RequireCors("Default")
            .WithName("CreateSessionTicket")
            .Produces<SessionTicketResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }
}
