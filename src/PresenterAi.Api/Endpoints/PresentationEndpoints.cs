using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using PresenterAi.Api.Errors;
using PresenterAi.Application.Content;
using PresenterAi.Contracts;
using PresenterAi.Contracts.Presentations;
using PresenterAi.Domain.Errors;

namespace PresenterAi.Api.Endpoints;

public static class PresentationEndpoints
{
    public static IEndpointRouteBuilder MapPresentationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/presentations").RequireAuthorization().RequireCors("Default");
        group.MapGet("", ListAsync)
            .WithName("ListPresentations")
            .Produces<ListResponse<PresentationSummary>>()
            .ProducesProblem(StatusCodes.Status500InternalServerError);
        group.MapGet("/{id}", LoadAsync)
            .WithName("GetPresentation")
            .Produces<PresentationDetail>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        IPresentationRepository repository,
        HttpContext context,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize < 1 || pageSize > 100)
        {
            return Problems.Create(context, ErrorCodes.ValidationFailed, StatusCodes.Status400BadRequest,
                "The page must be at least 1 and pageSize must be between 1 and 100.");
        }

        try
        {
            var ownerId = OwnerId(principal);
            var result = await repository.ListAsync(ownerId, page, pageSize, cancellationToken).ConfigureAwait(false);
            var items = result.Items
                .Select(row => new PresentationSummary(
                    row.Id, row.Title, row.SlideCount, row.Deck, row.Driver, row.Error))
                .ToArray();
            return Results.Ok(new ListResponse<PresentationSummary>(items, page, pageSize, result.Total));
        }
        catch (Exception)
        {
            return Problems.Create(context, ErrorCodes.InternalError, StatusCodes.Status500InternalServerError,
                "The presentations could not be loaded.");
        }
    }

    private static async Task<IResult> LoadAsync(
        [FromRoute] string id,
        ClaimsPrincipal principal,
        IPresentationRepository repository,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var presentation = await repository.LoadAsync(OwnerId(principal), id, cancellationToken).ConfigureAwait(false);
            var meta = presentation.Meta;
            return Results.Ok(new PresentationDetail(
                presentation.Id,
                new PresentationMetaDto(meta.Id, meta.Title, meta.Deck, meta.Driver, meta.Voice, meta.Context, meta.AdvanceSilenceMs, meta.ChunkChars, meta.MaxMinutes),
                presentation.Slides.Select(slide => new SlideDto(slide.Index, slide.Number, slide.Title, slide.Narration, slide.Notes)).ToArray(),
                !string.IsNullOrEmpty(presentation.Context)));
        }
        catch (FileNotFoundException)
        {
            return Problems.NotFound(context, ErrorCodes.PresentationNotFound, "The requested presentation was not found.");
        }
        catch (DirectoryNotFoundException)
        {
            return Problems.NotFound(context, ErrorCodes.PresentationNotFound, "The requested presentation was not found.");
        }
        catch (ArgumentException)
        {
            return Problems.Create(context, ErrorCodes.ValidationFailed, StatusCodes.Status400BadRequest,
                "The presentation id is invalid.");
        }
        catch (ScriptParseException)
        {
            return Problems.Create(context, ErrorCodes.PresentationInvalidScript, StatusCodes.Status400BadRequest,
                "The presentation script is invalid.");
        }
        catch (Exception)
        {
            return Problems.Create(context, ErrorCodes.InternalError, StatusCodes.Status500InternalServerError,
                "The presentation could not be loaded.");
        }
    }

    private static string OwnerId(ClaimsPrincipal principal) =>
        principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated principal has no subject.");
}
