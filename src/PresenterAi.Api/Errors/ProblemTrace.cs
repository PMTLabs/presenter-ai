using System.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http;

namespace PresenterAi.Api.Errors;

/// <summary>
/// The single source of the <c>traceId</c> carried by every Problem Details response (conventions §5): the
/// request's W3C activity id when the host started one, otherwise a freshly generated W3C id — and in both
/// cases the same value is written to the <c>traceparent</c> response header before the body is produced.
/// Both producers (<see cref="DomainExceptionHandler"/> and <see cref="Problems"/>) go through here so the
/// header can never be missing on one path and present on the other.
/// </summary>
internal static class ProblemTrace
{
    private const string ItemKey = "PresenterAi.ProblemTrace";

    /// <summary>
    /// Registered with <c>AddProblemDetails</c>: the framework's Problem Details writer (used by
    /// <c>Results.Problem</c> and every other <c>IProblemDetailsService</c> caller) overwrites <c>traceId</c>
    /// with <c>Activity.Current?.Id ?? TraceIdentifier</c>, so the value is re-applied here, after the
    /// defaults, to keep the body equal to the <c>traceparent</c> header.
    /// </summary>
    public static void Configure(ProblemDetailsOptions options) =>
        options.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions["traceId"] = Apply(context.HttpContext);

    public static string Apply(HttpContext context)
    {
        if (context.Items.TryGetValue(ItemKey, out var cached) && cached is string existing)
            return existing;

        var activity = context.Features.Get<IHttpActivityFeature>()?.Activity ?? Activity.Current;
        var traceId = activity?.Id
            ?? $"00-{ActivityTraceId.CreateRandom()}-{ActivitySpanId.CreateRandom()}-00";

        context.Items[ItemKey] = traceId;
        if (!context.Response.HasStarted)
            context.Response.Headers["traceparent"] = traceId;
        return traceId;
    }
}
