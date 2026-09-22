using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PresenterAi.Api.Auth;
using PresenterAi.Api.Middleware;
using PresenterAi.Api.Endpoints;
using PresenterAi.Api.Realtime;
using PresenterAi.Infrastructure.Content;
using PresenterAi.Infrastructure.Identity;
using PresenterAi.Application.Content;
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
builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddRedis(builder.Configuration);
builder.Services.AddHttpClient("sso");
builder.Services.AddOptions<JwtSettings>()
    .Bind(builder.Configuration.GetSection(JwtSettings.SectionName))
    .Validate(settings => !string.IsNullOrWhiteSpace(settings.Issuer), "Missing required setting: Jwt:Issuer")
    .Validate(settings => !string.IsNullOrWhiteSpace(settings.Audience), "Missing required setting: Jwt:Audience")
    .Validate(settings => Encoding.UTF8.GetBytes(settings.SecretKey).Length >= 32, "Jwt:SecretKey must be at least 32 bytes")
    .Validate(settings => settings.AccessTokenMinutes > 0, "Jwt:AccessTokenMinutes must be positive")
    .Validate(settings => settings.RefreshTokenDays > 0, "Jwt:RefreshTokenDays must be positive")
    .ValidateOnStart();
builder.Services.AddOptions<OAuthSettings>()
    .Bind(builder.Configuration.GetSection(OAuthSettings.SectionName))
    .Validate(settings => OAuthSettingsValidator.Validate(settings) is null, "OAuth settings are invalid when a provider is enabled.")
    .ValidateOnStart();
builder.Services.AddOptions<OAuthOptions>()
    .Bind(builder.Configuration.GetSection("OAuth"));
builder.Services.AddSingleton<SignInPolicy>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<SsoService>();
builder.Services.AddFileContent(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddLiveSessions();
builder.Services.AddPresenter();
builder.Services.AddPresenterBridge();
var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>() ?? new JwtSettings();
var jwtSigningKey = Encoding.UTF8.GetBytes(jwtSettings.SecretKey);
if (jwtSigningKey.Length < 32)
    jwtSigningKey = System.Security.Cryptography.SHA256.HashData(jwtSigningKey);
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
}).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtSettings.Issuer,
        ValidAudience = jwtSettings.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(jwtSigningKey),
        ClockSkew = TimeSpan.FromSeconds(30)
    };
    options.Events = new JwtBearerEvents
    {
        OnChallenge = async context =>
        {
            context.HandleResponse();
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await Problems.Create(context.HttpContext, ErrorCodes.AuthRequired, StatusCodes.Status401Unauthorized,
                "Authentication is required to access this resource.").ExecuteAsync(context.HttpContext);
        }
    };
});
builder.Services.AddAuthorization();
builder.Services.AddCors(options => options.AddPolicy("Refresh", policy =>
{
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));
builder.Services.AddAuthRateLimiting(builder.Configuration);

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

var contentOptions = app.Services.GetRequiredService<IOptions<ContentOptions>>().Value;
var webRoot = Path.GetFullPath(contentOptions.WebRoot, app.Environment.ContentRootPath);
var hasWebRoot = File.Exists(Path.Combine(webRoot, "index.html"));
app.UseStaticFiles(new StaticFileOptions
{
    // The deck root comes from the same IDeckStore the content layer registers (T16 wiring audit: no other consumer).
    FileProvider = new PhysicalFileProvider(app.Services.GetRequiredService<IDeckStore>().DeckRoot),
    RequestPath = "/decks",
    ServeUnknownFileTypes = true
});
if (hasWebRoot)
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(webRoot),
        OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
    });
}
else
{
    app.Logger.LogInformation("web root {WebRoot} not found — API only; run \"bun run dev:app\" for the UI", webRoot);
}
// Routing must come AFTER the static file middleware: with the implicit UseRouting at the top of the
// pipeline the "/decks/{**path}" 404 endpoint is selected first and StaticFileMiddleware then skips
// every deck file (found by the T11 Chrome run: "Deck not found: /ricoh/index.html").
app.UseRouting();
app.UseCors();
app.UseAuthRateLimitHeaders();
app.UseRateLimiter();
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthEndpoints();
app.MapPresentationEndpoints();
app.MapConfigEndpoints();
app.MapAuthEndpoints(builder.Configuration, app.Environment);
app.MapSessionEndpoints();
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

    if (path.StartsWith("/v1/", StringComparison.Ordinal)
        || path == "/ws"
        || path.StartsWith("/openapi/", StringComparison.Ordinal)
        || path == "/health")
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    if (!hasWebRoot)
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
    if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Postgres")))
        throw new InvalidOperationException("Missing required setting: ConnectionStrings:Postgres");
    if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Redis")))
        throw new InvalidOperationException("Missing required setting: ConnectionStrings:Redis");
    _ = app.Services.GetRequiredService<IOptions<JwtSettings>>().Value;
    _ = app.Services.GetRequiredService<IOptions<OAuthSettings>>().Value;
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
catch (InvalidOperationException ex) when (!isTesting && ex.Message.StartsWith("Missing required setting:", StringComparison.Ordinal))
{
    Console.Error.WriteLine($"Configuration invalid: {ex.Message}");
    return 1;
}

return 0;

public partial class Program;
