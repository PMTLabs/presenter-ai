using PresenterAi.Api.Errors;
using PresenterAi.Application.Content;
using PresenterAi.Contracts.Presentations;

namespace PresenterAi.Api.Endpoints;

public static class PresentationEndpoints
{
    public static IEndpointRouteBuilder MapPresentationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/presentations").RequireAuthorization();
        group.MapGet("", ListAsync)
            .WithName("ListPresentations")
            .Produces<IReadOnlyList<PresentationSummary>>();
        group.MapGet("/{id}", LoadAsync)
            .WithName("GetPresentation")
            .Produces<PresentationDetail>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(IPresentationRepository repository, CancellationToken cancellationToken)
    {
        try
        {
            var rows = await repository.ListAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(rows.Select(row => new PresentationSummary(
                row.Id, row.Title, row.SlideCount, row.Deck, row.Driver, row.Error)));
        }
        catch (Exception exception)
        {
            return NodeErrors.Json(StatusCodes.Status500InternalServerError, exception.Message);
        }
    }

    private static async Task<IResult> LoadAsync(string id, IPresentationRepository repository, CancellationToken cancellationToken)
    {
        try
        {
            var presentation = await repository.LoadAsync(id, cancellationToken).ConfigureAwait(false);
            var meta = presentation.Meta;
            return Results.Ok(new PresentationDetail(
                presentation.Id,
                new PresentationMetaDto(meta.Id, meta.Title, meta.Deck, meta.Driver, meta.Voice, meta.Context, meta.AdvanceSilenceMs, meta.ChunkChars),
                presentation.Slides.Select(slide => new SlideDto(slide.Index, slide.Number, slide.Title, slide.Narration, slide.Notes)).ToArray(),
                !string.IsNullOrEmpty(presentation.Context)));
        }
        catch (ArgumentException exception)
        {
            return NodeErrors.Json(StatusCodes.Status400BadRequest, exception.Message);
        }
        catch (FileNotFoundException)
        {
            return NodeErrors.Json(StatusCodes.Status404NotFound, $"presentation \"{id}\" not found");
        }
        catch (DirectoryNotFoundException)
        {
            return NodeErrors.Json(StatusCodes.Status404NotFound, $"presentation \"{id}\" not found");
        }
        catch (Exception exception)
        {
            return NodeErrors.Json(StatusCodes.Status400BadRequest, exception.Message);
        }
    }
}
