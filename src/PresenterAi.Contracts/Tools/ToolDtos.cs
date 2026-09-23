using System.Text.Json.Serialization;

namespace PresenterAi.Contracts.Tools;

public sealed record ToolSettingsResponse(bool WebSearchEnabled);

public sealed record UpdateToolSettingsRequest(bool WebSearchEnabled);

public sealed record ServerView(
    Guid Id,
    string Name,
    string Url,
    string AuthKind,
    string Status,
    string? LastErrorCode,
    bool AlwaysAsk,
    bool HasCredential,
    DateTimeOffset? LastConnectedAt);

public sealed record CreateToolServerRequest(string Name, string Url);

public sealed record UpdateToolServerRequest(string? Name = null, bool? AlwaysAsk = null);

public sealed record SaveToolCredentialRequest(string HeaderName, string HeaderValue);

public sealed record StartToolOAuthRequest(string? ClientId = null, string? ClientSecret = null);

public sealed record ToolOAuthStartResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? AuthorizationUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ServerView? Server = null);

public sealed record CompleteToolOAuthRequest(string Code, string State, string? Iss = null);

public sealed record TestToolServerResponse(
    bool Ok,
    int ToolCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorCode = null);

public sealed record ToolItemView(
    string Name,
    string? Title,
    string? Description,
    bool ReadOnly,
    bool AlwaysAsk);

public sealed record UpdateToolOverrideRequest(bool AlwaysAsk);
