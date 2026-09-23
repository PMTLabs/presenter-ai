using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PresenterAi.Application.Tools.External;

namespace PresenterAi.Infrastructure.Tools.Mcp;

public sealed class McpConnectorException(string code, Exception? inner = null) : Exception(code, inner)
{
    public string Code { get; } = code;
}

public sealed class McpConnection : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<McpClient>> _reconnectFunc;
    private readonly Func<string, CancellationToken, Task>? _setStatusFunc;
    private McpClient _client;

    public ToolConnection Server { get; }
    public McpClient Client => _client;
    public McpTokenState? TokenState { get; }

    public McpConnection(
        ToolConnection server,
        McpClient client,
        McpTokenState? tokenState,
        Func<CancellationToken, Task<McpClient>> reconnectFunc,
        Func<string, CancellationToken, Task>? setStatusFunc = null)
    {
        Server = server;
        _client = client;
        TokenState = tokenState;
        _reconnectFunc = reconnectFunc;
        _setStatusFunc = setStatusFunc;
    }

    public async Task<McpClient> ReconnectAsync(CancellationToken cancellationToken = default)
    {
        var newClient = await _reconnectFunc(cancellationToken);
        var old = _client;
        _client = newClient;
        try { await old.DisposeAsync(); } catch { }
        return _client;
    }

    public async Task SetStatusAsync(string status, CancellationToken cancellationToken = default)
    {
        if (_setStatusFunc != null)
        {
            await _setStatusFunc(status, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
    }
}

public sealed class McpTokenState
{
    private readonly Func<CancellationToken, Task<OAuthTokenCredential>> _refreshFunc;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private string _accessToken;
    private DateTimeOffset? _expiresAt;

    public string AccessToken => _accessToken;
    public DateTimeOffset? ExpiresAt => _expiresAt;

    public McpTokenState(string accessToken, DateTimeOffset? expiresAt, Func<CancellationToken, Task<OAuthTokenCredential>> refreshFunc)
    {
        _accessToken = accessToken;
        _expiresAt = expiresAt;
        _refreshFunc = refreshFunc;
    }

    public async Task EnsureFreshAsync(CancellationToken cancellationToken = default)
    {
        if (_expiresAt.HasValue && _expiresAt.Value <= DateTimeOffset.UtcNow.AddSeconds(60))
        {
            await RefreshAsync(cancellationToken);
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var cred = await _refreshFunc(cancellationToken);
            _accessToken = cred.AccessToken;
            _expiresAt = cred.ExpiresAt;
        }
        finally
        {
            _semaphore.Release();
        }
    }
}

public sealed class McpConnector(
    IHttpMessageHandlerFactory handlerFactory,
    IOutboundAddressPolicy policy,
    CredentialProtector protector,
    McpOAuthService? oauthService,
    IOptions<ExternalToolsOptions> options,
    IServiceScopeFactory scopeFactory,
    Func<string, Guid, CancellationToken, Task<OAuthTokenCredential>>? customRefresh = null)
{
    public async Task<McpConnection> ConnectAsync(
        string ownerId,
        ToolConnection server,
        ToolCredential? rawCredential,
        IReadOnlyDictionary<string, bool>? overrides = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var uri = OutboundUrlValidator.Validate(server.Url, policy);

            string? headerName = null;
            string? headerValue = null;
            McpTokenState? tokenState = null;

            if (server.AuthKind == "header" && rawCredential != null)
            {
                var read = protector.Unprotect(ownerId, server.Id, rawCredential);
                if (read.Payload == null)
                {
                    await UpdateStatusAsync(ownerId, server.Id, "needs_reconnect", read.ErrorCode ?? "credential_unreadable", cancellationToken);
                    throw new McpConnectorException(read.ErrorCode ?? "credential_unreadable");
                }
                using var doc = JsonDocument.Parse(read.Payload);
                headerValue = doc.RootElement.TryGetProperty("value", out var v) ? v.GetString() : null;
                headerName = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : "Authorization";
                if (string.IsNullOrEmpty(headerName)) headerName = "Authorization";
            }
            else if (server.AuthKind == "oauth" && rawCredential != null)
            {
                var read = protector.Unprotect(ownerId, server.Id, rawCredential);
                if (read.Payload == null)
                {
                    await UpdateStatusAsync(ownerId, server.Id, "needs_reconnect", read.ErrorCode ?? "credential_unreadable", cancellationToken);
                    throw new McpConnectorException(read.ErrorCode ?? "credential_unreadable");
                }
                var oauthCred = JsonSerializer.Deserialize<OAuthTokenCredential>(read.Payload);
                if (oauthCred == null)
                {
                    await UpdateStatusAsync(ownerId, server.Id, "needs_reconnect", "credential_unreadable", cancellationToken);
                    throw new McpConnectorException("credential_unreadable");
                }

                Func<CancellationToken, Task<OAuthTokenCredential>> refreshFunc = customRefresh != null
                    ? ct => customRefresh(ownerId, server.Id, ct)
                    : (oauthService != null
                        ? ct => oauthService.RefreshAsync(ownerId, server.Id, ct)
                        : throw new InvalidOperationException("OAuth refresh service not configured"));

                tokenState = new McpTokenState(
                    oauthCred.AccessToken,
                    oauthCred.ExpiresAt,
                    refreshFunc);

                // Refresh if expiring within 60s
                if (tokenState.ExpiresAt.HasValue && tokenState.ExpiresAt.Value <= DateTimeOffset.UtcNow.AddSeconds(60))
                {
                    try
                    {
                        await tokenState.RefreshAsync(cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        await UpdateStatusAsync(ownerId, server.Id, "needs_reconnect", "auth", cancellationToken);
                        throw new McpConnectorException("auth", ex);
                    }
                }
            }

            var client = await CreateClientAsync(uri, server.AuthKind, headerName, headerValue, tokenState, cancellationToken);

            Func<CancellationToken, Task<McpClient>> reconnectFunc = async ct =>
            {
                return await CreateClientAsync(uri, server.AuthKind, headerName, headerValue, tokenState, ct);
            };

            Func<string, CancellationToken, Task> setStatusFunc = async (status, ct) =>
            {
                await UpdateStatusAsync(ownerId, server.Id, status, "auth", ct);
            };

            return new McpConnection(server, client, tokenState, reconnectFunc, setStatusFunc);
        }
        catch (Exception ex) when (ex is not McpConnectorException && ex is not OperationCanceledException)
        {
            throw SafeException(ex);
        }
    }

    private async Task<McpClient> CreateClientAsync(
        Uri uri,
        string authKind,
        string? headerName,
        string? headerValue,
        McpTokenState? tokenState,
        CancellationToken cancellationToken)
    {
        var innerHandler = handlerFactory.CreateHandler("mcp");
        var authHandler = new McpHttpAuthHandler(
            authKind,
            headerName,
            headerValue,
            tokenState,
            onUnauthorized: async ct =>
            {
                if (tokenState != null)
                {
                    await tokenState.RefreshAsync(ct);
                }
            })
        {
            InnerHandler = innerHandler
        };

        var httpClient = new HttpClient(authHandler, disposeHandler: true);
        var timeoutSec = options.Value.Mcp.CallTimeoutSeconds;

        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = uri,
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromSeconds(timeoutSec)
        };

        var transport = new HttpClientTransport(
            transportOptions,
            httpClient,
            NullLoggerFactory.Instance,
            ownsHttpClient: true);

        return await McpClient.CreateAsync(
            transport,
            clientOptions: null,
            loggerFactory: NullLoggerFactory.Instance,
            cancellationToken: cancellationToken);
    }

    private async Task UpdateStatusAsync(string ownerId, Guid serverId, string status, string? errorCode, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IToolConnectionRepository>();
            await repo.SetStatusAsync(ownerId, serverId, status, errorCode, ct);
        }
        catch
        {
            // best-effort status update
        }
    }

    private static Exception SafeException(Exception exception) => exception switch
    {
        OutboundGuardException og => new McpConnectorException(og.Code, og),
        McpOAuthException oe => new McpConnectorException(oe.Code, oe),
        HttpRequestException hre when hre.StatusCode == HttpStatusCode.Unauthorized => new McpConnectorException("auth", hre),
        _ => new McpConnectorException("unreachable", exception)
    };
}

public sealed class McpHttpAuthHandler : DelegatingHandler
{
    private readonly string _authKind;
    private readonly string? _headerName;
    private readonly string? _headerValue;
    private readonly McpTokenState? _tokenState;
    private readonly Func<CancellationToken, Task>? _onUnauthorized;

    public McpHttpAuthHandler(
        string authKind,
        string? headerName,
        string? headerValue,
        McpTokenState? tokenState,
        Func<CancellationToken, Task>? onUnauthorized = null)
    {
        _authKind = authKind;
        _headerName = headerName;
        _headerValue = headerValue;
        _tokenState = tokenState;
        _onUnauthorized = onUnauthorized;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        AttachAuth(request);

        byte[]? contentBytes = null;
        if (request.Content != null)
        {
            contentBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var freshContent = new ByteArrayContent(contentBytes);
            foreach (var (header, values) in request.Content.Headers)
            {
                freshContent.Headers.TryAddWithoutValidation(header, values);
            }
            request.Content = freshContent;
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized && _tokenState != null && _onUnauthorized != null)
        {
            if (!IsToolCall(contentBytes))
            {
                try
                {
                    await _onUnauthorized(cancellationToken);
                }
                catch
                {
                    return response;
                }

                var retryRequest = CloneRequest(request, contentBytes);
                AttachAuth(retryRequest);
                response = await base.SendAsync(retryRequest, cancellationToken);
            }
        }
        return response;
    }

    private static bool IsToolCall(byte[]? contentBytes)
    {
        if (contentBytes == null || contentBytes.Length == 0) return false;
        var text = Encoding.UTF8.GetString(contentBytes);
        return text.Contains("\"tools/call\"");
    }

    private void AttachAuth(HttpRequestMessage request)
    {
        if (_authKind == "header" && !string.IsNullOrEmpty(_headerValue))
        {
            var name = !string.IsNullOrEmpty(_headerName) ? _headerName : "Authorization";
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, _headerValue);
        }
        else if (_authKind == "oauth" && _tokenState != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokenState.AccessToken);
        }
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage original, byte[]? contentBytes)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy
        };

        foreach (var (header, values) in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header, values);
        }

        if (contentBytes != null)
        {
            var content = new ByteArrayContent(contentBytes);
            if (original.Content != null)
            {
                foreach (var (header, values) in original.Content.Headers)
                {
                    content.Headers.TryAddWithoutValidation(header, values);
                }
            }
            clone.Content = content;
        }

        return clone;
    }
}
