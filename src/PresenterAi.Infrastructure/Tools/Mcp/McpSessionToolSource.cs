using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PresenterAi.Application.Tools;
using PresenterAi.Application.Tools.External;

namespace PresenterAi.Infrastructure.Tools.Mcp;

public sealed class McpSessionToolSource(
    IServiceScopeFactory scopeFactory,
    McpConnector connector,
    IOptions<ExternalToolsOptions> options,
    ILogger<McpSessionToolSource>? logger = null) : ISessionToolSource
{
    public async Task<SessionToolSet> LoadAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        var createdConnections = new ConcurrentBag<McpConnection>();
        bool succeeded = false;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IToolConnectionRepository>();

            var servers = await repo.ListAsync(ownerId, cancellationToken);
            var webSearchEnabled = await repo.GetWebSearchEnabledAsync(ownerId, cancellationToken);
            var hostedTools = webSearchEnabled
                ? new[] { new JsonObject { ["type"] = "web_search" } }
                : Array.Empty<JsonObject>();

            if (servers.Count == 0)
            {
                succeeded = true;
                return new SessionToolSet(tools: [], hostedTools: hostedTools, notes: [], disposable: null);
            }

            var overridesTasks = servers.ToDictionary(
                s => s.Id,
                s => repo.GetOverridesAsync(ownerId, s.Id, cancellationToken));
            await Task.WhenAll(overridesTasks.Values);

            var budgetMs = options.Value.Mcp.StartBudgetMs;
            using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budgetCts.CancelAfter(budgetMs);

            var serverTasks = servers.Select(server =>
                LoadServerAsync(ownerId, server, overridesTasks[server.Id].Result, createdConnections, budgetCts.Token, cancellationToken)
            ).ToArray();

            var serverResults = await Task.WhenAll(serverTasks);

            var allTools = new List<ITool>();
            var allNotes = new List<string>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var result in serverResults)
            {
                allNotes.AddRange(result.Notes);

                foreach (var tool in result.Tools)
                {
                    var finalName = McpTool.FormatToolName(tool.Server.Slug, tool.RawToolName, usedNames);
                    tool.SetName(finalName);
                    allTools.Add(tool);
                }
            }

            var compositeDisposable = new CompositeAsyncDisposable(createdConnections.Cast<IAsyncDisposable>().ToArray());
            succeeded = true;
            return new SessionToolSet(allTools, hostedTools, allNotes, compositeDisposable);
        }
        finally
        {
            if (!succeeded)
            {
                foreach (var conn in createdConnections)
                {
                    try { await conn.DisposeAsync(); } catch { }
                }
            }
        }
    }

    private async Task<ServerResult> LoadServerAsync(
        string ownerId,
        ToolConnection server,
        IReadOnlyDictionary<string, bool> overrides,
        ConcurrentBag<McpConnection> createdConnections,
        CancellationToken budgetToken,
        CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var tools = new List<McpTool>();
        uint? credentialVersion = null;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IToolConnectionRepository>();

            var credential = await repo.GetCredentialAsync(ownerId, server.Id, budgetToken);
            credentialVersion = credential?.Version;
            var connection = await connector.ConnectAsync(ownerId, server, credential, overrides, sessionId: null, budgetToken);
            createdConnections.Add(connection);

            var listed = await connection.Client.ListToolsAsync(cancellationToken: budgetToken);
            var capped = listed.Take(64).ToList();

            foreach (var clientTool in capped)
            {
                var schemaJson = clientTool.ProtocolTool.InputSchema.GetRawText();
                if (Encoding.UTF8.GetByteCount(schemaJson) > 4096)
                {
                    notes.Add(
                        $"tools: {UntrustedLogText.Sanitize(server.Name)} skipped " +
                        $"{UntrustedLogText.Sanitize(clientTool.Name)} (schema exceeds 4 KiB)");
                    continue;
                }

                var schemaObj = JsonNode.Parse(schemaJson) as JsonObject ?? new JsonObject();
                var overrideAlwaysAsk = overrides.TryGetValue(clientTool.Name, out var aa) && aa;
                var requiresConfirmation = McpTool.CalculateRequiresConfirmation(
                    clientTool.ProtocolTool.Annotations?.ReadOnlyHint,
                    server.AlwaysAsk,
                    overrideAlwaysAsk);

                var timeout = TimeSpan.FromSeconds(options.Value.Mcp.CallTimeoutSeconds);

                var tool = new McpTool(
                    server,
                    clientTool.Name,
                    clientTool.Title,
                    clientTool.Description,
                    schemaObj,
                    requiresConfirmation,
                    timeout,
                    callToolAsync: async (name, args, ct) =>
                    {
                        return await connection.Client.CallToolAsync(name, args, cancellationToken: ct);
                    },
                    refreshTokenAsync: connection.TokenState != null
                        ? async ct => await connection.TokenState.RefreshAsync(ct)
                        : null,
                    reconnectAsync: async ct =>
                    {
                        await connection.ReconnectAsync(ct);
                    },
                    setStatusAsync: async (status, ct) =>
                    {
                        await connection.SetStatusAsync(status, ct);
                    },
                    ensureFreshTokenAsync: connection.TokenState != null
                        ? async ct => await connection.TokenState.EnsureFreshAsync(ct)
                        : null,
                    logger: logger);

                tools.Add(tool);
            }

            await repo.SetStatusIfCredentialVersionAsync(ownerId, server.Id, credentialVersion,
                "connected", null, cancellationToken);
            return new ServerResult(tools, notes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            notes.Add($"tools: {UntrustedLogText.Sanitize(server.Name)} skipped (timeout)");
            await UpdateStatusAsync(ownerId, server.Id, credentialVersion, "error", "timeout", cancellationToken);
            return new ServerResult(tools, notes);
        }
        catch (Exception ex)
        {
            string code = ex switch
            {
                McpConnectorException ce => ce.Code,
                McpOAuthException oe => oe.Code,
                OutboundGuardException ge => ge.Code,
                HttpRequestException hre when hre.StatusCode == HttpStatusCode.Unauthorized => "auth",
                _ => "unreachable"
            };

            string status = McpFailure.Status(code);

            notes.Add($"tools: {UntrustedLogText.Sanitize(server.Name)} skipped ({code})");
            await UpdateStatusAsync(ownerId, server.Id, credentialVersion, status, code, cancellationToken);
            return new ServerResult(tools, notes);
        }
    }

    private async Task UpdateStatusAsync(string ownerId, Guid serverId, uint? version,
        string status, string? code, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IToolConnectionRepository>();
            await repo.SetStatusIfCredentialVersionAsync(ownerId, serverId, version, status, code, ct);
        }
        catch
        {
            // best-effort status write
        }
    }

    private sealed record ServerResult(IReadOnlyList<McpTool> Tools, IReadOnlyList<string> Notes);

    private sealed class CompositeAsyncDisposable(IReadOnlyList<IAsyncDisposable> disposables) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var item in disposables)
            {
                try { await item.DisposeAsync(); } catch { }
            }
        }
    }
}
