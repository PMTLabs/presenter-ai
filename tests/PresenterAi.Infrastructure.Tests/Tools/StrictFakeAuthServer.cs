using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PresenterAi.Infrastructure.Tests.Tools;

// Loopback TLS fixture, deliberately strict about every value that binds the authorization to the resource.
public sealed class StrictFakeAuthServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public X509Certificate2 Certificate { get; }
    public string Url => _app.Urls.Single().TrimEnd('/');
    public string Resource => Url + "/mcp";
    public string RedirectUri { get; set; } = "https://app.example/tools/oauth/callback";
    public bool ClientMetadata { get; set; } = true;
    public bool Registration { get; set; } = true;
    public bool S256 { get; set; } = true;
    public bool IncompatibleRegistration { get; set; }
    public bool WrongResource { get; set; }
    public bool BlockedMetadata { get; set; }
    public bool PathMetadata { get; set; } = true;
    public bool RootMetadata { get; set; } = true;
    public bool OidcOnly { get; set; }
    public string RegisteredAuthMethod { get; set; } = "none";
    public int RefreshDelayMs { get; set; }
    public int SecondRefreshDelayMs { get; set; }
    public bool RejectRefresh { get; set; }
    public bool RejectCode { get; set; }
    public bool RejectMcpToken { get; set; }
    public int ExpiresIn { get; set; } = 3600;
    public int ToolsListCount;
    public string? ErrorDescription { get; set; }
    public string? RegistrationSecret { get; set; }
    public string? AuthorizationCode { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public string? EchoHeader { get; set; }
    public int TokenCount;
    public int RefreshCount;
    public int MetadataFetchCount;
    public int RegistrationCount;
    public int AuthorizeCount;
    public ConcurrentQueue<string> DiscoveryRequests { get; } = new();
    public ConcurrentQueue<string> Registrations { get; } = new();
    public ConcurrentQueue<string> Challenges { get; } = new();
    public ConcurrentQueue<string> CodeChallenges { get; } = new();
    public ConcurrentQueue<string> TokenGrants { get; } = new();
    public ConcurrentQueue<string> TokenResources { get; } = new();
    private readonly ConcurrentDictionary<string, Authorization> _codes = new();
    private readonly ConcurrentDictionary<string, string> _clients = new();
    private readonly ConcurrentDictionary<string, bool> _refresh = new();
    private int _serial;
    private StrictFakeAuthServer(WebApplication app, X509Certificate2 certificate)
    {
        _app = app;
        Certificate = certificate;
        _clients["registered-client"] = "client_secret_post";
    }

    public string ChallengeHeader => $"Bearer resource_metadata=\"{(BlockedMetadata ? "https://169.254.169.254/metadata" : Url + "/.well-known/oauth-protected-resource/mcp")}\", scope=\"read write\"";

    public static async Task<StrictFakeAuthServer> StartAsync()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(20));
        var cert = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(cert)));
        var app = builder.Build();
        var fake = new StrictFakeAuthServer(app, cert);
        fake.Map();
        await app.StartAsync();
        return fake;
    }

    private void Map()
    {
        _app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            if (RejectMcpToken || !ctx.Request.Headers.Authorization.ToString().StartsWith($"Bearer {AccessToken ?? "access-"}", StringComparison.Ordinal))
            {
                Challenges.Enqueue(ChallengeHeader);
                ctx.Response.Headers.WWWAuthenticate = ChallengeHeader;
                return Results.Unauthorized();
            }
            using var body = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
            var method = body.RootElement.GetProperty("method").GetString();
            if (method == "tools/list") Interlocked.Increment(ref ToolsListCount);
            var id = body.RootElement.TryGetProperty("id", out var requestId) ? requestId.Clone() : default;
            if (id.ValueKind == System.Text.Json.JsonValueKind.Undefined) return Results.Accepted();
            object result = method switch
            {
                "discover" => new { supportedVersions = new[] { "2025-06-18", "2026-07-28" }, capabilities = new { tools = new { } } },
                "initialize" => new { protocolVersion = "2025-06-18", supportedVersions = new[] { "2025-06-18", "2026-07-28" }, capabilities = new { tools = new { } },
                    serverInfo = new { name = "fake", version = "1" } },
                "tools/list" => new { tools = Array.Empty<object>() },
                _ => new { supportedVersions = new[] { "2025-06-18", "2026-07-28" }, capabilities = new { tools = new { } } }
            };
            return Results.Json(new { jsonrpc = "2.0", id, result });
        });
        _app.MapGet("/guard-ok", () => Results.Text("ok"));
        _app.MapGet("/guard-redirect", () => Results.Redirect("/guard-ok", permanent: false));
        _app.MapGet("/guard-oversize", () => Results.Text(new string('x', 1024 * 1024 + 1)));
        _app.MapGet("/.well-known/oauth-protected-resource/mcp", (HttpContext ctx) => Protected(ctx, PathMetadata));
        _app.MapGet("/.well-known/oauth-protected-resource", (HttpContext ctx) => Protected(ctx, RootMetadata));
        _app.MapGet("/.well-known/oauth-authorization-server", (HttpContext ctx) => AuthMetadata(ctx, !OidcOnly));
        _app.MapGet("/.well-known/openid-configuration", (HttpContext ctx) => AuthMetadata(ctx, true));
        _app.MapGet("/v1/tools/oauth/client-metadata.json", (HttpContext ctx) =>
        {
            Interlocked.Increment(ref MetadataFetchCount);
            return Results.Json(new { client_id = Url + "/v1/tools/oauth/client-metadata.json", client_name = "Presenter AI",
                redirect_uris = new[] { RedirectUri }, grant_types = new[] { "authorization_code", "refresh_token" },
                response_types = new[] { "code" }, token_endpoint_auth_method = "none" });
        });
        _app.MapPost("/register", async (HttpContext ctx) =>
        {
            Interlocked.Increment(ref RegistrationCount);
            var document = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
            var raw = document.RootElement.GetRawText();
            Registrations.Enqueue(raw);
            var root = document.RootElement;
            if (root.GetProperty("redirect_uris")[0].GetString() != RedirectUri ||
                root.GetProperty("token_endpoint_auth_method").GetString() != "none" ||
                !root.GetProperty("grant_types").EnumerateArray().Any(e => e.GetString() == "authorization_code"))
                return Results.BadRequest();
            var id = "dynamic-" + Interlocked.Increment(ref _serial);
            _clients[id] = RegisteredAuthMethod;
            return Results.Json(new { client_id = id,
                client_secret = RegisteredAuthMethod == "none" ? null : RegistrationSecret ?? "dynamic-secret",
                token_endpoint_auth_method = IncompatibleRegistration ? "private_key_jwt" : RegisteredAuthMethod,
                redirect_uris = IncompatibleRegistration ? Array.Empty<string>() : new[] { RedirectUri },
                grant_types = new[] { "authorization_code", "refresh_token" } });
        });
        _app.MapGet("/authorize", async (HttpContext ctx) =>
        {
            Interlocked.Increment(ref AuthorizeCount);
            var q = ctx.Request.Query;
            var id = q["client_id"].ToString();
            if (id == Url + "/v1/tools/oauth/client-metadata.json")
            {
                // A real AS fetches the HTTPS client ID document before accepting it.
                using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback =
                    (_, certificate, _, _) => certificate?.GetCertHashString() == Certificate.GetCertHashString() };
                using var client = new HttpClient(handler);
                using var response = await client.GetAsync(id);
                if (!response.IsSuccessStatusCode) return Results.BadRequest();
                var document = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
                if (document.GetProperty("client_id").GetString() != id ||
                    document.GetProperty("redirect_uris")[0].GetString() != RedirectUri)
                    return Results.BadRequest();
                _clients[id] = "none";
            }
            if (!_clients.ContainsKey(id) || q["redirect_uri"] != RedirectUri ||
                q["resource"] != Resource || q["code_challenge_method"] != "S256" ||
                q["response_type"] != "code" || string.IsNullOrEmpty(q["state"])) return Results.BadRequest();
            var challenge = q["code_challenge"].ToString();
            CodeChallenges.Enqueue(challenge);
            var code = AuthorizationCode ?? "code-" + Interlocked.Increment(ref _serial);
            _codes[code] = new Authorization(id, challenge, q["redirect_uri"].ToString());
            return Results.Redirect($"{RedirectUri}?code={code}&state={q["state"]}&iss={Uri.EscapeDataString(Url)}");
        });
        _app.MapPost("/token", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var grant = form["grant_type"].ToString();
            TokenGrants.Enqueue(grant);
            TokenResources.Enqueue(form["resource"].ToString());
            var id = form["client_id"].ToString();
            if (ctx.Request.Headers.Authorization.ToString().StartsWith("Basic ", StringComparison.Ordinal))
            {
                try
                {
                    var raw = Encoding.UTF8.GetString(Convert.FromBase64String(ctx.Request.Headers.Authorization.ToString()[6..]));
                    var parts = raw.Split(':', 2);
                    id = Uri.UnescapeDataString(parts[0]);
                    if (parts.Length != 2 || Uri.UnescapeDataString(parts[1]) != (RegistrationSecret ?? "dynamic-secret")) return Results.BadRequest();
                }
                catch (FormatException) { return Results.BadRequest(); }
            }
            if (!_clients.TryGetValue(id, out var method) || form["resource"] != Resource ||
                method == "client_secret_post" && (ctx.Request.Headers.ContainsKey("Authorization") ||
                    form["client_secret"] != (id == "registered-client" ? string.Concat("registered", "-secret") : RegistrationSecret ?? "dynamic-secret")) ||
                method == "client_secret_basic" && (!ctx.Request.Headers.Authorization.ToString().StartsWith("Basic ", StringComparison.Ordinal) ||
                    form.ContainsKey("client_secret")) ||
                method == "none" && (form.ContainsKey("client_secret") || ctx.Request.Headers.ContainsKey("Authorization")))
                return Results.BadRequest();
            if (EchoHeader is not null) ctx.Response.Headers["X-Echo"] = EchoHeader;
            if (grant == "authorization_code")
            {
                Interlocked.Increment(ref TokenCount);
                if (RejectCode) return Results.BadRequest(new { error = "invalid_grant", error_description = ErrorDescription });
                if (!_codes.TryRemove(form["code"].ToString(), out var auth) || auth.ClientId != id ||
                    form["redirect_uri"] != auth.RedirectUri ||
                    Hash(form["code_verifier"].ToString()) != auth.Challenge) return Results.BadRequest();
            }
            else if (grant == "refresh_token")
            {
                var count = Interlocked.Increment(ref RefreshCount);
                var delay = count == 2 ? SecondRefreshDelayMs : RefreshDelayMs;
                if (delay > 0) await Task.Delay(delay);
                if (RejectRefresh || !_refresh.TryRemove(form["refresh_token"].ToString(), out _))
                    return Results.BadRequest(new { error = "invalid_grant", error_description = ErrorDescription });
            }
            else return Results.BadRequest();
            var serial = Interlocked.Increment(ref _serial);
            var refresh = RefreshToken ?? "refresh-" + serial;
            _refresh[refresh] = true;
            return Results.Json(new { access_token = AccessToken ?? "access-" + serial, refresh_token = refresh,
                token_type = "Bearer", expires_in = ExpiresIn, scope = "read write" });
        });
    }

    private IResult Protected(HttpContext ctx, bool enabled)
    {
        DiscoveryRequests.Enqueue(ctx.Request.Path);
        return enabled ? Results.Json(new { resource = WrongResource ? Url + "/wrong" : Resource,
            authorization_servers = new[] { Url } }) : Results.NotFound();
    }

    private IResult AuthMetadata(HttpContext ctx, bool enabled)
    {
        DiscoveryRequests.Enqueue(ctx.Request.Path);
        return enabled ? Results.Json(new { issuer = Url, authorization_endpoint = Url + "/authorize",
            token_endpoint = Url + "/token", registration_endpoint = Registration ? Url + "/register" : null,
            client_id_metadata_document_supported = ClientMetadata,
            code_challenge_methods_supported = S256 ? new[] { "S256" } : new[] { "plain" },
            authorization_response_iss_parameter_supported = true,
            scopes_supported = new[] { "read", "write" } }) : Results.NotFound();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Certificate.Dispose();
    }

    private static string Hash(string verifier) => Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private sealed record Authorization(string ClientId, string Challenge, string RedirectUri);
}
