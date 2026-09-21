using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;
using PresenterAi.Api.Endpoints;
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

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Extensions ??= new Dictionary<string, Microsoft.OpenApi.IOpenApiExtension>();
    document.Extensions["x-error-codes"] = new ErrorCodesOpenApiExtension();
    return Task.CompletedTask;
}));

var app = builder.Build();
app.UseExceptionHandler();
app.MapHealthEndpoints();
app.MapOpenApi("/openapi/v1.json");

if (app.Environment.IsEnvironment("Testing"))
{
    app.MapGet("/__test/throw", (HttpContext _) => throw new InvalidOperationException("SECRET-MESSAGE-123"));
    app.MapGet("/__test/domain-error", (HttpContext _) => throw new DomainException(
        ErrorCodes.PresentationNotFound,
        StatusCodes.Status404NotFound,
        "The requested test presentation does not exist."));
}

try
{
    app.Run();
}
catch (OptionsValidationException ex)
{
    // Startup validation (ValidateOnStart) — exit 1 naming the missing setting. The host logger is
    // already disposed by the time the exception reaches here, so write to stderr directly.
    Console.Error.WriteLine($"Configuration invalid: {string.Join("; ", ex.Failures)}");
    return 1;
}

return 0;

public partial class Program;
