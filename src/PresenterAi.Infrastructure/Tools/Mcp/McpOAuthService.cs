using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using PresenterAi.Application.Tools.External;
using StackExchange.Redis;

namespace PresenterAi.Infrastructure.Tools.Mcp;

public sealed class McpOAuthException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record OAuthStartResult(string AuthorizationUrl);
public sealed record OAuthTokenCredential(string Kind, string AccessToken, string? RefreshToken, string TokenType,
    string? Scope, DateTimeOffset? ExpiresAt, string ClientId, string? ClientSecret,
    string TokenAuthMethod, string Registration, string Issuer, string TokenEndpoint, string Resource);

public sealed class McpOAuthService(
    IHttpClientFactory clients, IOutboundAddressPolicy policy, IServiceScopeFactory scopes,
    McpOAuthStateStore states, CredentialProtector protector, IConnectionMultiplexer redis,
    IOptions<ExternalToolsOptions> options, IConfiguration configuration)
{
    private readonly IDatabase _database = redis.GetDatabase();
    private readonly string _baseUrl = configuration["OAuth:ApiBaseUrl"] ?? string.Empty;
    private string RedirectUri => options.Value.OAuthRedirectUri ??
        new Uri(new Uri(_baseUrl.TrimEnd('/') + "/"), "tools/oauth/callback").AbsoluteUri;
    private string? MetadataClientId => Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https"
        ? new Uri(new Uri(_baseUrl.TrimEnd('/') + "/"), "v1/tools/oauth/client-metadata.json").AbsoluteUri : null;

    // Task 4 calls this before StartAsync; no credentials are attached to the probe.
    public async Task<string?> ProbeChallengeAsync(string serverUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            var uri = OutboundUrlValidator.Validate(serverUrl, policy);
            using var http = clients.CreateClient("mcp-oauth");
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = JsonContent.Create(new
                {
                    jsonrpc = "2.0", id = 1, method = "initialize",
                    @params = new { protocolVersion = "2025-06-18", capabilities = new { },
                        clientInfo = new { name = "presenter-ai", version = "1" } }
                })
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return response.Headers.WwwAuthenticate.ToString();
            if (response.IsSuccessStatusCode) return null;
            throw new McpOAuthException("tools_unreachable");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Safe(exception);
        }
    }

    // challenge is the WWW-Authenticate value from the unauthenticated initialize probe (Task 4).
    public async Task<OAuthStartResult> StartAsync(string ownerId, Guid serverId, string? challenge = null,
        string? clientId = null, string? clientSecret = null, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IToolConnectionRepository>();
            var server = await repo.GetAsync(ownerId, serverId, cancellationToken) ?? throw new McpOAuthException("tools_server_not_found");
            var resource = OutboundUrlValidator.Validate(server.Url, policy).AbsoluteUri;
            using var http = clients.CreateClient("mcp-oauth");
            JsonElement protectedResource = default;
            var metadataUrl = ChallengeValue(challenge, "resource_metadata");
            var paths = new List<string>();
            if (metadataUrl is not null) paths.Add(metadataUrl);
            var uri = new Uri(resource);
            paths.Add($"{uri.GetLeftPart(UriPartial.Authority)}/.well-known/oauth-protected-resource{uri.AbsolutePath.TrimEnd('/')}");
            paths.Add($"{uri.GetLeftPart(UriPartial.Authority)}/.well-known/oauth-protected-resource");
            foreach (var path in paths.Distinct())
            {
                var (document, status) = await MetadataAsync(http, path, cancellationToken);
                if (status == HttpStatusCode.OK) { protectedResource = document; break; }
            }
            if (protectedResource.ValueKind != JsonValueKind.Object || Field(protectedResource, "resource") != resource)
                throw new McpOAuthException("tools_oauth_unsupported");
            var issuers = Strings(protectedResource, "authorization_servers");
            if (issuers.Length == 0) throw new McpOAuthException("tools_oauth_unsupported");
            var issuer = OutboundUrlValidator.Validate(issuers[0], policy).AbsoluteUri.TrimEnd('/');
            var issuerUri = new Uri(issuer);
            var baseUri = issuerUri.GetLeftPart(UriPartial.Authority);
            var suffix = issuerUri.AbsolutePath.TrimEnd('/');
            JsonElement authorization = default;
            foreach (var path in new[] { $"{baseUri}/.well-known/oauth-authorization-server{suffix}",
                $"{baseUri}{suffix}/.well-known/openid-configuration" })
            {
                var (document, status) = await MetadataAsync(http, path, cancellationToken);
                if (status == HttpStatusCode.OK) { authorization = document; break; }
            }
            if (authorization.ValueKind != JsonValueKind.Object || Field(authorization, "issuer")?.TrimEnd('/') != issuer ||
                !Strings(authorization, "code_challenge_methods_supported").Contains("S256"))
                throw new McpOAuthException("tools_oauth_unsupported");
            var authorize = OutboundUrlValidator.Validate(Field(authorization, "authorization_endpoint") ?? "", policy);
            var token = OutboundUrlValidator.Validate(Field(authorization, "token_endpoint") ?? "", policy);
            var method = clientSecret is null ? "none" : "client_secret_post";
            var registration = "pre-registered";
            if (string.IsNullOrWhiteSpace(clientId))
            {
                if (authorization.TryGetProperty("client_id_metadata_document_supported", out var supported) &&
                    supported.ValueKind == JsonValueKind.True && MetadataClientId is not null)
                {
                    clientId = MetadataClientId;
                    clientSecret = null;
                    method = "none";
                    registration = "metadata document";
                }
                else if (Field(authorization, "registration_endpoint") is { } endpoint)
                {
                    var registrationUri = OutboundUrlValidator.Validate(endpoint, policy);
                    var request = new { client_name = "Presenter AI", redirect_uris = new[] { RedirectUri },
                        grant_types = new[] { "authorization_code", "refresh_token" }, response_types = new[] { "code" },
                        token_endpoint_auth_method = "none" };
                    var registered = await PostJsonAsync(http, registrationUri, request, cancellationToken);
                    clientId = Field(registered, "client_id");
                    method = Field(registered, "token_endpoint_auth_method") ?? "none";
                    clientSecret = Field(registered, "client_secret");
                    if (string.IsNullOrWhiteSpace(clientId) || !Strings(registered, "redirect_uris").Contains(RedirectUri) ||
                        !Strings(registered, "grant_types").Contains("authorization_code") ||
                        method is not ("none" or "client_secret_post" or "client_secret_basic") ||
                        method != "none" && string.IsNullOrEmpty(clientSecret))
                        throw new McpOAuthException("tools_oauth_unsupported");
                    registration = "dynamic";
                }
                else throw new McpOAuthException("tools_oauth_client_required");
            }
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var state = Base64Url(RandomNumberGenerator.GetBytes(32));
            await states.SaveAsync(state, new McpOAuthState(ownerId, serverId, verifier, issuer, token.AbsoluteUri,
                clientId!, clientSecret, method, resource, RedirectUri, registration,
                authorization.TryGetProperty("authorization_response_iss_parameter_supported", out var issuerSupported) &&
                issuerSupported.ValueKind == JsonValueKind.True));
            var scopeValue = ChallengeValue(challenge, "scope") ?? string.Join(' ', Strings(authorization, "scopes_supported"));
            var parameters = new Dictionary<string, string> { ["response_type"] = "code", ["client_id"] = clientId!,
                ["redirect_uri"] = RedirectUri, ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
                ["code_challenge_method"] = "S256", ["state"] = state, ["resource"] = resource };
            if (!string.IsNullOrEmpty(scopeValue)) parameters["scope"] = scopeValue;
            return new OAuthStartResult(authorize.AbsoluteUri + (authorize.Query.Length == 0 ? "?" : "&") +
                string.Join('&', parameters.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Safe(exception);
        }
    }

    public async Task<Guid> CompleteAsync(string ownerId, string code, string state, string? iss = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var saved = await states.TakeAsync(state);
            if (saved is null || saved.OwnerId != ownerId) throw new McpOAuthException("tools_oauth_state_invalid");
            if (iss is not null && iss != saved.Issuer || saved.RequireIssuer && iss != saved.Issuer)
                throw new McpOAuthException("tools_oauth_failed");
            using var scope = scopes.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IToolConnectionRepository>();
            if (await repo.GetAsync(ownerId, saved.ServerId, cancellationToken) is null)
                throw new McpOAuthException("tools_server_not_found");
            using var http = clients.CreateClient("mcp-oauth");
            var values = new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code,
                ["redirect_uri"] = saved.RedirectUri, ["code_verifier"] = saved.Verifier, ["resource"] = saved.Resource };
            var token = await ExchangeAsync(http, saved.TokenEndpoint, values, saved.ClientId, saved.ClientSecret,
                saved.TokenAuthMethod, cancellationToken);
            var credential = MakeCredential(token, saved.ClientId, saved.ClientSecret, saved.TokenAuthMethod,
                saved.Registration, saved.Issuer, saved.TokenEndpoint, saved.Resource);
            var encrypted = protector.Protect(ownerId, saved.ServerId, JsonSerializer.Serialize(credential));
            if (!await repo.SaveCredentialAsync(ownerId, saved.ServerId, encrypted.Ciphertext, encrypted.KeyId,
                credential.ExpiresAt, cancellationToken: cancellationToken)) throw new McpOAuthException("tools_oauth_failed");
            await repo.SetStatusAsync(ownerId, saved.ServerId, "connected", null, "oauth", cancellationToken);
            return saved.ServerId;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Safe(exception);
        }
    }

    public async Task<OAuthTokenCredential> RefreshAsync(string ownerId, Guid serverId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IToolConnectionRepository>();
            var original = await repo.GetCredentialAsync(ownerId, serverId, cancellationToken) ?? throw new McpOAuthException("tools_auth");
            var current = Decode(original, ownerId, serverId);
            var leaseKey = (RedisKey)$"mcp:refresh:{serverId}";
            var lease = Base64Url(RandomNumberGenerator.GetBytes(24));
            if (!await _database.StringSetAsync(leaseKey, lease, TimeSpan.FromSeconds(15), When.NotExists))
            {
                for (var i = 0; i < 30; i++)
                {
                    await Task.Delay(100, cancellationToken);
                    var changed = await FreshAsync(ownerId, serverId, original.Version, cancellationToken);
                    if (changed is not null) return changed;
                    if (!await _database.KeyExistsAsync(leaseKey)) break;
                }
                throw new McpOAuthException("tools_auth");
            }
            try
            {
                try
                {
                    if (string.IsNullOrEmpty(current.RefreshToken)) throw new McpOAuthException("tools_oauth_invalid_grant");
                    using var http = clients.CreateClient("mcp-oauth");
                    var values = new Dictionary<string, string> { ["grant_type"] = "refresh_token",
                        ["refresh_token"] = current.RefreshToken, ["resource"] = current.Resource };
                    var token = await ExchangeAsync(http, current.TokenEndpoint, values, current.ClientId,
                        current.ClientSecret, current.TokenAuthMethod, cancellationToken);
                    var updated = MakeCredential(token, current.ClientId, current.ClientSecret, current.TokenAuthMethod,
                        current.Registration, current.Issuer, current.TokenEndpoint, current.Resource, current.RefreshToken);
                    var encrypted = protector.Protect(ownerId, serverId, JsonSerializer.Serialize(updated));
                    if (await repo.SaveCredentialAsync(ownerId, serverId, encrypted.Ciphertext, encrypted.KeyId,
                        updated.ExpiresAt, original.Version, cancellationToken))
                    {
                        await repo.SetStatusAsync(ownerId, serverId, "connected", null, cancellationToken);
                        return updated;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var winner = await FreshAsync(ownerId, serverId, original.Version, cancellationToken);
                    if (winner is not null) return winner;
                    await repo.SetStatusAsync(ownerId, serverId, "needs_reconnect", "oauth_invalid_grant", cancellationToken);
                    throw Safe(exception);
                }
                var replacement = await FreshAsync(ownerId, serverId, original.Version, cancellationToken);
                if (replacement is not null) return replacement;
                throw new McpOAuthException("tools_auth");
            }
            finally
            {
                await _database.ScriptEvaluateAsync("if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end",
                    [leaseKey], [(RedisValue)lease]);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Safe(exception);
        }
    }

    private async Task<OAuthTokenCredential?> FreshAsync(string ownerId, Guid serverId, uint version, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var credential = await scope.ServiceProvider.GetRequiredService<IToolConnectionRepository>()
            .GetCredentialAsync(ownerId, serverId, ct);
        return credential is not null && credential.Version != version ? Decode(credential, ownerId, serverId) : null;
    }

    private OAuthTokenCredential Decode(ToolCredential credential, string ownerId, Guid serverId)
    {
        var result = protector.Unprotect(ownerId, serverId, credential);
        if (result.Payload is null) throw new McpOAuthException(result.ErrorCode ?? "credential_unreadable");
        return JsonSerializer.Deserialize<OAuthTokenCredential>(result.Payload) ?? throw new McpOAuthException("credential_unreadable");
    }

    private static OAuthTokenCredential MakeCredential(JsonElement token, string clientId, string? secret, string method,
        string registration, string issuer, string endpoint, string resource, string? oldRefresh = null)
    {
        var access = Field(token, "access_token");
        if (string.IsNullOrEmpty(access) || Field(token, "token_type") is not "Bearer")
            throw new McpOAuthException("tools_oauth_failed");
        var expires = token.TryGetProperty("expires_in", out var seconds) && seconds.TryGetInt32(out var n) && n > 0
            ? DateTimeOffset.UtcNow.AddSeconds(n) : (DateTimeOffset?)null;
        return new("oauth", access, Field(token, "refresh_token") ?? oldRefresh, "Bearer", Field(token, "scope"),
            expires, clientId, secret, method, registration, issuer, endpoint, resource);
    }

    private static async Task<JsonElement> ExchangeAsync(HttpClient http, string endpoint, Dictionary<string, string> values,
        string clientId, string? secret, string method, CancellationToken ct)
    {
        if (method == "none") values["client_id"] = clientId;
        else if (method == "client_secret_post") { values["client_id"] = clientId; values["client_secret"] = secret ?? ""; }
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(values) };
        if (method == "client_secret_basic")
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(clientId)}:{Uri.EscapeDataString(secret ?? "")}")));
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new McpOAuthException("tools_oauth_invalid_grant");
        return await ReadJsonAsync(response, ct);
    }

    private async Task<(JsonElement Document, HttpStatusCode Status)> MetadataAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await OutboundMetadata.GetAsync(http, url, policy, ct);
        return response.IsSuccessStatusCode ? (await ReadJsonAsync(response, ct), response.StatusCode) : (default, response.StatusCode);
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient http, Uri url, object body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(url, body, ct);
        if (!response.IsSuccessStatusCode) throw new McpOAuthException("tools_oauth_unsupported");
        return await ReadJsonAsync(response, ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct) =>
        (await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct)).RootElement.Clone();
    private static string? Field(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static string[] Strings(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array
        ? property.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray() : [];
    private static string? ChallengeValue(string? challenge, string name)
    {
        if (challenge is null) return null;
        var match = System.Text.RegularExpressions.Regex.Match(challenge,
            $"(?:^|[,\\s]){name}=\\\"([^\\\"]+)\\\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static McpOAuthException Safe(Exception exception) => exception switch
    {
        McpOAuthException oauth => oauth,
        OutboundGuardException guard => new McpOAuthException(guard.Code),
        _ => new McpOAuthException("tools_oauth_failed")
    };
}
