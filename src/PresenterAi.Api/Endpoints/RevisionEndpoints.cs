using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using PresenterAi.Api.Errors;
using PresenterAi.Application.Scripts;
using PresenterAi.Application.Scripts.Revisions;
using PresenterAi.Contracts;
using PresenterAi.Contracts.Presentations;

namespace PresenterAi.Api.Endpoints;

/// <summary>
/// Script version history of a presentation (plan 010 §4.3). Owner-scoped through the store: another owner's
/// presentation is indistinguishable from a missing one (404 <c>presentation.not_found</c>).
/// </summary>
public static class RevisionEndpoints
{
    public static IEndpointRouteBuilder MapRevisionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/presentations/{id}/revisions").RequireAuthorization().RequireCors("Default");
        group.MapGet("", ListAsync)
            .WithName("ListPresentationRevisions")
            .Produces<ListResponse<RevisionSummary>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
        group.MapGet("/{number}", GetAsync)
            .WithName("GetPresentationRevision")
            .Produces<RevisionDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
        group.MapPost("/{number}/revert", RevertAsync)
            .WithName("RevertPresentationRevision")
            .Produces<RevertResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        [FromRoute] string id,
        ClaimsPrincipal principal,
        [FromServices] IPresentationRevisionStore store,
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
            var list = await store.ListAsync(OwnerId(principal), id, page, pageSize, cancellationToken).ConfigureAwait(false);
            if (list is null)
            {
                return PresentationNotFound(context);
            }

            var items = list.Items.Select(info => ToSummary(info, list.CurrentVersion)).ToArray();
            return Results.Ok(new ListResponse<RevisionSummary>(items, page, pageSize, list.Total));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Problems.Create(context, ErrorCodes.InternalError, StatusCodes.Status500InternalServerError,
                "The script versions could not be loaded.");
        }
    }

    private static async Task<IResult> GetAsync(
        [FromRoute] string id,
        [FromRoute] int number,
        ClaimsPrincipal principal,
        [FromServices] IPresentationRevisionStore store,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var ownerId = OwnerId(principal);
            var head = await store.GetHeadAsync(ownerId, id, cancellationToken).ConfigureAwait(false);
            if (head is null)
            {
                return PresentationNotFound(context);
            }

            var revision = await store.GetAsync(ownerId, id, number, cancellationToken).ConfigureAwait(false);
            if (revision is null)
            {
                return RevisionNotFound(context);
            }

            var slides = ScriptParser.Parse(revision.Script, id).Slides;
            var baseSlides = revision.Info.BaseVersion is int baseVersion
                && await store.GetAsync(ownerId, id, baseVersion, cancellationToken).ConfigureAwait(false) is { } baseRevision
                    ? ScriptParser.Parse(baseRevision.Script, id).Slides
                    : null;
            var info = revision.Info;
            return Results.Ok(new RevisionDetail(
                info.Number,
                info.Source,
                info.CreatedAt,
                info.Summary,
                info.BaseVersion,
                info.RevertedFrom,
                info.ChangedSlides,
                info.Number == head.Version,
                slides.Select(slide => new RevisionSlide(slide.Index, slide.Number, slide.Title, slide.Narration)).ToArray(),
                baseSlides is null ? [] : Changes(baseSlides, slides)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Problems.Create(context, ErrorCodes.InternalError, StatusCodes.Status500InternalServerError,
                "The script version could not be loaded.");
        }
    }

    private static async Task<IResult> RevertAsync(
        [FromRoute] string id,
        [FromRoute] int number,
        ClaimsPrincipal principal,
        [FromServices] IScriptRevisionService revisions,
        HttpContext context)
    {
        RevertResult result;
        try
        {
            var userId = OwnerId(principal);
            result = await revisions.RevertAsync(userId, id, number, userId, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Problems.Create(context, ErrorCodes.InternalError, StatusCodes.Status500InternalServerError,
                "The script version could not be restored.");
        }

        return result switch
        {
            RevertResult.Reverted reverted => Results.Created(
                $"/v1/presentations/{Uri.EscapeDataString(id)}/revisions/{reverted.Revision.Number}",
                new RevertResponse(
                    ToSummary(reverted.Revision, reverted.Revision.Number),
                    reverted.PendingEdits.Select(edit => new PendingEditDto(edit.Id, edit.SlideIndexes, edit.Status)).ToArray())),
            RevertResult.Conflict => Problems.Create(context, ErrorCodes.ConcurrencyConflict, StatusCodes.Status409Conflict,
                "The script changed while it was being restored. Refresh and try again."),
            RevertResult.RevisionNotFound => RevisionNotFound(context),
            _ => PresentationNotFound(context)
        };
    }

    /// <summary>Slides whose narration differs between the base version and this one, in slide order.</summary>
    private static RevisionChange[] Changes(IReadOnlyList<Slide> before, IReadOnlyList<Slide> after) =>
        Enumerable.Range(0, Math.Max(before.Count, after.Count))
            .Select(index => (Before: index < before.Count ? before[index] : null, After: index < after.Count ? after[index] : null))
            .Where(pair => !string.Equals(pair.Before?.Narration, pair.After?.Narration, StringComparison.Ordinal))
            .Select(pair => new RevisionChange(
                (pair.After ?? pair.Before)!.Index,
                (pair.After ?? pair.Before)!.Title,
                pair.Before?.Narration,
                pair.After?.Narration))
            .ToArray();

    private static RevisionSummary ToSummary(RevisionInfo info, int currentVersion) => new(
        info.Number,
        info.Source,
        info.CreatedAt,
        info.Summary,
        info.BaseVersion,
        info.RevertedFrom,
        info.ChangedSlides,
        info.Number == currentVersion);

    private static IResult PresentationNotFound(HttpContext context) =>
        Problems.NotFound(context, ErrorCodes.PresentationNotFound, "The requested presentation was not found.");

    private static IResult RevisionNotFound(HttpContext context) =>
        Problems.NotFound(context, ErrorCodes.RevisionNotFound, "The requested script version was not found.");

    private static string OwnerId(ClaimsPrincipal principal) =>
        principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated principal has no subject.");
}
