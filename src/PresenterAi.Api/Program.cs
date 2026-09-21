using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PresenterAi.Api.Auth;
using PresenterAi.Api.Endpoints;
using PresenterAi.Api.Realtime;
using PresenterAi.Infrastructure.Content;
using PresenterAi.Infrastructure.Live;
using PresenterAi.Api.Errors;
using PresenterAi.Contracts;
using PresenterAi.Domain.Errors;
using PresenterAi.Infrastructure;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .WriteTo.Console());

builder.Services.AddUpstreamOptions(builder.Configuration);
builder.Services.AddFileContent(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddLiveSessions();
builder.Services.AddPresenterBridge();
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = DevAuthHandler.SchemeName;
    options.DefaultChallengeScheme = DevAuthHandler.SchemeName;
}).AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddProblemDetails(ProblemTrace.Configure);
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Extensions ??= new Dictionary<string, Microsoft.OpenApi.IOpenApiExtension>();
    document.Extensions["x-error-codes"] = new ErrorCodesOpenApiExtension();
    return Task.CompletedTask;
}));

var app = builder.Build();
app.UseExceptionHandler();
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();

var contentOptions = app.Services.GetRequiredService<IOptions<ContentOptions>>().Value;
var rootDir = Path.GetFullPath(contentOptions.RootDir, app.Environment.ContentRootPath);
var webRoot = Path.GetFullPath(contentOptions.WebRoot, app.Environment.ContentRootPath);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(Path.Combine(rootDir, "decks")),
    RequestPath = "/decks",
    ServeUnknownFileTypes = true
});
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(webRoot),
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
});

app.MapHealthEndpoints();
app.MapPresentationEndpoints();
app.MapConfigEndpoints();
app.MapPresenterBridge();
app.MapOpenApi("/openapi/v1.json");
app.MapGet("/decks/{**path}", async (HttpContext context) =>
{
    var path = context.Request.Path.Value ?? "/decks";
    context.Response.StatusCode = StatusCodes.Status404NotFound;
    await context.Response.WriteAsync($"Deck not found: {path["/decks".Length..]}. Put your deck under decks/<name>/.");
}).ExcludeFromDescription();
app.MapFallback(async context =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    if (path.StartsWith("/decks", StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync($"Deck not found: {path["/decks".Length..]}. Put your deck under decks/<name>/.");
        return;
    }

    if (path.StartsWith("/api/", StringComparison.Ordinal)
        || path == "/ws"
        || path.StartsWith("/openapi/", StringComparison.Ordinal)
        || path == "/health")
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.Headers.CacheControl = "no-cache";
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(Path.Combine(webRoot, "index.html"));
}).ExcludeFromDescription();

if (app.Environment.IsEnvironment("Testing"))
{
    app.MapGet("/__test/throw", (HttpContext _) => throw new InvalidOperationException("SECRET-MESSAGE-123"));
    app.MapGet("/__test/domain-error", (HttpContext _) => throw new DomainException(
        ErrorCodes.PresentationNotFound,
        StatusCodes.Status404NotFound,
        "The requested test presentation does not exist."));
}

// Captured before Run(): the host (and its IHostEnvironment) is disposed by the time the filter runs.
var isTesting = app.Environment.IsEnvironment("Testing");
try
{
    // Resolve the validated options before the host starts: a missing setting then fails here, with one
    // clean line, instead of inside Host.StartAsync where the hosting logger prints a stack trace first.
    _ = app.Services.GetRequiredService<IOptions<UpstreamOptions>>().Value;
    app.Run();
}
catch (OptionsValidationException ex) when (!isTesting)
{
    // Exit 1 naming the missing setting instead of an unhandled exception crash. The host logger may be
    // disposed here, so write to stderr directly. Under WebApplicationFactory ("Testing") the exception
    // must propagate so StartupTests can observe it.
    Console.Error.WriteLine($"Configuration invalid: {string.Join("; ", ex.Failures)}");
    return 1;
}

return 0;

public partial class Program;
