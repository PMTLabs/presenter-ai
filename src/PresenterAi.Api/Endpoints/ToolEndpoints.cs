using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PresenterAi.Api.Errors;
using PresenterAi.Api.Middleware;
using PresenterAi.Application.Tools.External;
using PresenterAi.Contracts;
using PresenterAi.Contracts.Tools;
using PresenterAi.Infrastructure.Tools;
using PresenterAi.Infrastructure.Tools.Mcp;

namespace PresenterAi.Api.Endpoints;

public static class ToolEndpoints
{
    public static IEndpointRouteBuilder MapToolEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/tools").RequireAuthorization().RequireCors("Default");

        group.MapGet("/settings", GetSettingsAsync)
            .WithName("GetToolSettings")
            .Produces<ToolSettingsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPut("/settings", UpdateSettingsAsync)
            .WithName("UpdateToolSettings")
            .Produces<ToolSettingsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/servers", ListServersAsync)
            .WithName("ListToolServers")
            .Produces<ServerView[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/servers", CreateServerAsync)
            .RequireRateLimiting(AuthRateLimiting.Policies.Tools)
            .WithName("CreateToolServer")
            .Produces<ServerView>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPatch("/servers/{id}", UpdateServerAsync)
            .WithName("UpdateToolServer")
            .Produces<ServerView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/servers/{id}", DeleteServerAsync)
            .WithName("DeleteToolServer")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/servers/{id}/credential", SaveCredentialAsync)
            .RequireRateLimiting(AuthRateLimiting.Policies.Tools)
            .WithName("SaveToolCredential")
            .Produces<ServerView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapDelete("/servers/{id}/credential", DisconnectCredentialAsync)
            .WithName("DisconnectToolCredential")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/servers/{id}/oauth/start", StartOAuthAsync)
            .RequireRateLimiting(AuthRateLimiting.Policies.Tools)
            .WithName("StartToolOAuth")
            .Produces<ToolOAuthStartResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/oauth/complete", CompleteOAuthAsync)
            .RequireRateLimiting(AuthRateLimiting.Policies.Tools)
            .WithName("CompleteToolOAuth")
            .Produces<ServerView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/servers/{id}/test", TestServerAsync)
            .RequireRateLimiting(AuthRateLimiting.Policies.Tools)
            .WithName("TestToolServer")
            .Produces<TestToolServerResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/servers/{id}/tools", ListToolsAsync)
            .RequireRateLimiting(AuthRateLimiting.Policies.Tools)
            .WithName("ListToolsForServer")
            .Produces<ToolItemView[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        group.MapPut("/servers/{id}/tools/{toolName}", SetToolOverrideAsync)
            .WithName("SetToolOverride")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> GetSettingsAsync(
        ClaimsPrincipal principal,
        IToolConnectionRepository repository,
        CancellationToken cancellationToken)
    {
        var ownerId = OwnerId(principal);
        var enabled = await repository.GetWebSearchEnabledAsync(ownerId, cancellationToken);
        return Results.Ok(new ToolSettingsResponse(enabled));
    }

    private static async Task<IResult> UpdateSettingsAsync(
        ClaimsPrincipal principal,
        [FromBody] UpdateToolSettingsRequest request,
        IToolConnectionRepository repository,
        CancellationToken cancellationToken)
    {
        var ownerId = OwnerId(principal);
        await repository.SetWebSearchEnabledAsync(ownerId, request.WebSearchEnabled, cancellationToken);
        return Results.Ok(new ToolSettingsResponse(request.WebSearchEnabled));
    }

    private static async Task<IResult> ListServersAsync(
        ClaimsPrincipal principal,
        IToolConnectionRepository repository,
        CancellationToken cancellationToken)
    {
        var ownerId = OwnerId(principal);
        var list = await repository.ListAsync(ownerId, cancellationToken);
        var views = list.Select(ToView).ToArray();
        return Results.Ok(views);
    }

    private static async Task<IResult> CreateServerAsync(
        ClaimsPrincipal principal,
        [FromBody] CreateToolServerRequest request,
        IToolConnectionRepository repository,
        IOutboundAddressPolicy policy,
        IOutboundDnsResolver resolver,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length is < 1 or > 40)
            return Problems.Create(context, ErrorCodes.ToolsNameInvalid, StatusCodes.Status400BadRequest, "The tool server name must be between 1 and 40 characters.");

        if (string.IsNullOrWhiteSpace(request.Url))
            return Problems.Create(context, ErrorCodes.ToolsUrlInvalid, StatusCodes.Status400BadRequest, "The tool server URL is invalid.");

        try
        {
            await OutboundUrlValidator.ValidateForSaveAsync(request.Url, policy, resolver, cancellationToken);
        }
        catch (OutboundGuardException ex)
        {
            var status = StatusCodes.Status400BadRequest;
            var detail = ex.Code == ErrorCodes.ToolsUrlBlocked
                ? "The tool server URL resolves to a blocked or non-public address."
                : "The tool server URL is invalid.";
            return Problems.Create(context, ex.Code, status, detail);
        }

        try
        {
            var ownerId = OwnerId(principal);
            var server = await repository.AddAsync(ownerId, request.Name, request.Url, cancellationToken);
            return Results.Created($"/v1/tools/servers/{server.Id}", ToView(server));
        }
        catch (InvalidOperationException ex) when (ex.Message == "tools_server_limit")
        {
            return Problems.Create(context, ErrorCodes.ToolsServerLimit, StatusCodes.Status400BadRequest, "The maximum limit of 10 tool servers per user has been reached.");
        }
    }

    private static async Task<IResult> UpdateServerAsync(
        [FromRoute] Guid id,
        [FromBody] UpdateToolServerRequest request,
        ClaimsPrincipal principal,
        IToolConnectionRepository repository,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var ownerId = OwnerId(principal);
        if (await repository.GetAsync(ownerId, id, cancellationToken) is null)
            return Problems.NotFound(context, ErrorCodes.ToolsServerNotFound, "The tool server was not found.");
        if (request.Name is not null && (request.Name.Length is < 1 or > 40 || string.IsNullOrWhiteSpace(request.Name)))
            return Problems.Create(context, ErrorCodes.ToolsNameInvalid, StatusCodes.Status400BadRequest, "The tool server name must be between 1 and 40 characters.");

        var updated = await repository.UpdateAsync(ownerId, id, request.Name, request.AlwaysAsk, cancellationToken);
        if (!updated)
            return Problems.NotFound(context, ErrorCodes.ToolsServerNotFound, "The tool server was not found.");

        var server = await repository.GetAsync(ownerId, id, cancellationToken);
        if (server is null)
            return Problems.NotFound(context, ErrorCodes.ToolsServerNotFound, "The tool server was not found.");

        return Results.Ok(ToView(server));
    }

    private static async Task<IResult> DeleteServerAsync(
        [FromRoute] Guid id,
        ClaimsPrincipal principal,
        IToolConnectionRepository repository,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var ownerId = OwnerId(principal);
        var deleted = await repository.RemoveAsync(ownerId, id, cancellationToken);
        if (!deleted)
            return Problems.NotFound(context, ErrorCodes.ToolsServerNotFound, "The tool server was not found.");

        return Results.NoContent();
    }

    private static async Task<IResult> SaveCredentialAsync(
        [FromRoute] Guid id,
        [FromBody] SaveToolCredentialRequest request,
        ClaimsPrincipal principal,
        IToolConnectionRepository repository,
        CredentialProtector protector,
        IOptions<ExternalToolsOptions> options,
        McpConnector connector,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var ownerId = OwnerId(principal);
        var server = await repository.GetAsync(ownerId, id, cancellationToken);
        if (server is null)
            return Problems.NotFound(context, ErrorCodes.ToolsServerNotFound, "The tool server was not found.");

        if (!IsValidHeaderName(request.HeaderName) || !IsValidHeaderValue(request.HeaderValue))
            return Problems.Create(context, ErrorCodes.ToolsHeaderInvalid, StatusCodes.Status400BadRequest, "Invalid header name or value.");

        if (string.IsNullOrEmpty(options.Value.CredentialKey))
            return Problems.Create(context, ErrorCodes.ToolsCredentialsUnavailable, StatusCodes.Status503ServiceUnavailable, "Tool credentials protection is unavailable.");

        var payload = JsonSerializer.Serialize(new { kind = "header", name = request.HeaderName, value = request.HeaderValue });
        var encrypted = protector.Protect(ownerId, id, payload);
        await repository.SaveCredentialAsync(ownerId, id, encrypted.Ciphertext, encrypted.KeyId, cancellationToken: cancellationToken);

        var updatedServer = server with { AuthKind = "header" };
        var rawCred = new ToolCredential(encrypted.Ciphertext, encrypted.KeyId, null, 1);
        try
        {
            await using var connection = await connector.ConnectAsync(ownerId, updatedServer, rawCred, cancellationToken: cancellationToken);
            await connection.Client.ListToolsAsync(cancellationToken: cancellationToken);
            await repository.SetStatusAsync(ownerId, id, "connected", null, "header", cancellationToken);
        }
        catch (McpConnectorException mex)
        {
            await repository.SetStatusAsync(ownerId, id, McpFailure.Status(mex.Code), mex.Code, "header", cancellationToken);
        }
        catch (Exception)
        {
            await repository.SetStatusAsync(ownerId, id, "error", "unreachable", "header", cancellationToken);
        }

        var finalServer = await repository.GetAsync(ownerId, id, cancellationToken);
        return Results.Ok(ToView(finalServer!));
    }

    private static async Task<IResult> DisconnectCredentialAsync(
        [FromRoute] Guid id,
        ClaimsPrincipal principal,
        IToolConnectionRepository repository,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var ownerId = OwnerId(principal);
        var server = await repository.GetAsync(ownerId, id, cancellationToken);
        if (server is null)
            return Problems.NotFound(context, ErrorCodes.ToolsServerNotFound, "The tool server was not found.");

        await repository.DisconnectAsync(ownerId, id, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> StartOAuthAsync(
        [FromRoute] Guid id,
        [FromBody] StartToolOAuthRequest? request,
        ClaimsPrincipal principal,
        IToolConnectionRepository repository,
        IOptions<ExternalToolsOptions> options,
        McpOAuthService oauthService,
        McpConnector connector,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var ownerId = OwnerId(principal);
        var server = await repository.GetAsync(ownerId, id, cancellationToken);
        if (server is null)
            return Problems.NotFound(context, ErrorCodes.ToolsServerNotFound, "The tool server was not found.");

        try
        {
            var challenge = await oauthService.ProbeChallengeAsync(server.Url, cancellationToken);
            if (challenge is null)
            {
                await using var connection = await connector.ConnectAsync(ownerId, server with { AuthKind = "none" }, null,
                    cancellationToken: cancellationToken);
                await connection.Client.ListToolsAsync(cancellationToken: cancellationToken);
                await repository.SetStatusAsync(ownerId, id, "connected", null, "none", cancellationToken);
                var updated = await repository.GetAsync(ownerId, id, cancellationToken);
                return Results.Ok(new ToolOAuthStartResponse(Server: ToView(updated!)));
            }

            if (string.IsNullOrEmpty(options.Value.CredentialKey))
                return Problems.Create(context, ErrorCodes.ToolsCredentialsUnavailable, StatusCodes.Status503ServiceUnavailable, "Tool credentials protection is unavailable.");
            var startResult = await oauthService.StartAsync(ownerId, id, challenge, request?.ClientId, request?.ClientSecret, cancellationToken);
            return Results.Ok(new ToolOAuthStartResponse(AuthorizationUrl: startResult.AuthorizationUrl));
        }
        catch (McpConnectorException ex)
        {
            await repository.SetStatusAsync(ownerId, id, McpFailure.Status(ex.Code), ex.Code, cancellationToken);
            return Problems.Create(context, McpFailure.ProblemCode(ex.Code), McpFailure.HttpStatus(ex.Code), "Tool server connection failed.");
        }
        catch (McpOAuthException ex)
        {
            var status = ex.Code == ErrorCodes.ToolsUnreachable ? StatusCodes.Status502BadGateway : StatusCodes.Status400BadRequest;
            return Problems.Create(context, ex.Code, status, "OAuth initialization failed.");
        }
        catch (OutboundGuardException og)
        {
            var status = og.Code == ErrorCodes.ToolsUnreachable ? StatusCodes.Status502BadGateway : StatusCodes.Status400BadRequest;
            return Problems.Create(context, og.Code, status, "Outbound connection refused.");
        }
    }

    private static async Task<IResult> CompleteOAuthAsync(
        [FromBody] CompleteToolOAuthRequest request,
        ClaimsPrincipal principal,
        IToolConnectionRepository repository,
        IOptions<ExternalToolsOptions> options,
        McpOAuthService oauthService,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.State))
            return Problems.Create(context, ErrorCodes.ToolsOAuthStateInvalid, StatusCodes.Status400BadRequest, "Invalid OAuth completion parameters.");

        if (string.IsNullOrEmpty(options.Value.CredentialKey))
            return Problems.Create(context, ErrorCodes.ToolsCredentialsUnavailable, StatusCodes.Status503ServiceUnavailable, "Tool credentials protection is unavailable.");

        var ownerId = OwnerId(principal);
        try
        {
            var serverId = await oauthService.CompleteAsync(ownerId, request.Code, request.State, request.Iss, cancellationToken);
            var server = await repository.GetAsync(ownerId, serverId, cancellationToken);
            return Results.Ok(ToView(server!));
        }
        catch (McpOAuthException ex)
        {
            return Problems.Create(context, ex.Code, StatusCodes.Status400BadRequest, "OAuth completion failed.");
        }
    }

    private static async Task<IResult> TestServerAsync(
        [FromRoute] Guid id,
        ClaimsPrincipal principal,
        IToolConnectionRepository repository,
        IOptions<ExternalToolsOptions> options,
        McpConnector connector,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var ownerId = OwnerId(principal);
        var server = await repository.GetAsync(ownerId, id, cancellationToken);
        if (server is null)
            return Problems.NotFound(context, ErrorCodes.ToolsServerNotFound, "The tool server was not found.");

        var credential = await repository.GetCredentialAsync(ownerId, id, cancellationToken);
        var overrides = await repository.GetOverridesAsync(ownerId, id, cancellationToken);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(options.Value.Mcp.CallTimeoutSeconds));

            await using var connection = await connector.ConnectAsync(ownerId, server, credential, overrides, cancellationToken: cts.Token);
            var tools = await connection.Client.ListToolsAsync(cancellationToken: cts.Token);
            var toolCount = tools.Count;

            await repository.SetStatusAsync(ownerId, id, "connected", null, cancellationToken);
            return Results.Ok(new TestToolServerResponse(Ok: true, ToolCount: toolCount));
        }
        catch (McpConnectorException mex)
        {
            await repository.SetStatusAsync(ownerId, id, McpFailure.Status(mex.Code), mex.Code, cancellationToken);
            return Results.Ok(new TestToolServerResponse(Ok: false, ToolCount: 0, ErrorCode: mex.Code));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await repository.SetStatusAsync(ownerId, id, "error", "timeout", cancellationToken);
            return Results.Ok(new TestToolServerResponse(Ok: false, ToolCount: 0, ErrorCode: "timeout"));
        }
        catch (Exception)
        {
            await repository.SetStatusAsync(ownerId, id, "error", "unreachable", cancellationToken);
            return Results.Ok(new TestToolServerResponse(Ok: false, ToolCount: 0, ErrorCode: "unreachable"));
        }
    }

    private static async Task<IResult> ListToolsAsync(
        [FromRoute] Guid id,
        ClaimsPrincipal principal,
        IToolConnectionRepository repository,
        McpConnector connector,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var ownerId = OwnerId(principal);
        var server = await repository.GetAsync(ownerId, id, cancellationToken);
        if (server is null)
            return Problems.NotFound(context, ErrorCodes.ToolsServerNotFound, "The tool server was not found.");

        var credential = await repository.GetCredentialAsync(ownerId, id, cancellationToken);
        var overrides = await repository.GetOverridesAsync(ownerId, id, cancellationToken);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            await using var connection = await connector.ConnectAsync(ownerId, server, credential, overrides, cancellationToken: cts.Token);
            var tools = await connection.Client.ListToolsAsync(cancellationToken: cts.Token);

            var views = tools.Select(t =>
            {
                var isReadOnly = t.ProtocolTool.Annotations?.ReadOnlyHint == true;
                var alwaysAsk = !isReadOnly || server.AlwaysAsk || (overrides.TryGetValue(t.Name, out var ov) && ov);
                return new ToolItemView(
                    t.Name,
                    t.Title,
                    t.Description,
                    isReadOnly,
                    alwaysAsk);
            }).ToArray();

            return Results.Ok(views);
        }
        catch (McpConnectorException mex)
        {
            await repository.SetStatusAsync(ownerId, id, McpFailure.Status(mex.Code), mex.Code, cancellationToken);
            return Problems.Create(context, McpFailure.ProblemCode(mex.Code), McpFailure.HttpStatus(mex.Code), "Tool server connection failed.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Problems.Create(context, ErrorCodes.ToolsUnreachable, StatusCodes.Status502BadGateway, "Tool server is unreachable (timeout).");
        }
        catch (Exception)
        {
            return Problems.Create(context, ErrorCodes.ToolsUnreachable, StatusCodes.Status502BadGateway, "Tool server is unreachable.");
        }
    }

    private static async Task<IResult> SetToolOverrideAsync(
        [FromRoute] Guid id,
        [FromRoute] string toolName,
        [FromBody] UpdateToolOverrideRequest request,
        ClaimsPrincipal principal,
        IToolConnectionRepository repository,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var ownerId = OwnerId(principal);
        var server = await repository.GetAsync(ownerId, id, cancellationToken);
        if (server is null)
            return Problems.NotFound(context, ErrorCodes.ToolsServerNotFound, "The tool server was not found.");

        if (string.IsNullOrWhiteSpace(toolName) || toolName.Length is < 1 or > 128)
            return Problems.Create(context, ErrorCodes.ValidationFailed, StatusCodes.Status400BadRequest, "The tool name is invalid.");

        var updated = await repository.SetOverrideAsync(ownerId, id, toolName, request.AlwaysAsk, cancellationToken);
        if (!updated)
            return Problems.NotFound(context, ErrorCodes.ToolsServerNotFound, "The tool server was not found.");

        return Results.NoContent();
    }

    private static ServerView ToView(ToolConnection c) =>
        new(c.Id, c.Name, c.Url, c.AuthKind, c.Status, c.LastErrorCode, c.AlwaysAsk, c.HasCredential, c.LastConnectedAt);

    private static string OwnerId(ClaimsPrincipal principal) =>
        principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated principal has no subject.");

    private static bool IsValidHeaderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Mcp-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        foreach (var c in name)
        {
            if (!IsTChar(c)) return false;
        }
        return true;
    }

    private static bool IsTChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or
        '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

    private static bool IsValidHeaderValue(string? value)
    {
        if (value is null) return false;
        if (value.Contains('\r') || value.Contains('\n')) return false;
        if (Encoding.UTF8.GetByteCount(value) > 4096) return false;
        return true;
    }
}
