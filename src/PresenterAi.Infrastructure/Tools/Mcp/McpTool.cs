using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using PresenterAi.Application.Tools;
using PresenterAi.Application.Tools.External;

namespace PresenterAi.Infrastructure.Tools.Mcp;

public sealed class McpTool : ITool
{
    private readonly ToolConnection _server;
    private readonly string _rawToolName;
    private readonly string _host;
    private readonly Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, Task<CallToolResult>> _callToolAsync;
    private readonly Func<CancellationToken, Task>? _refreshTokenAsync;
    private readonly Func<CancellationToken, Task>? _reconnectAsync;
    private readonly Func<string, CancellationToken, Task>? _setStatusAsync;
    private readonly Func<CancellationToken, Task>? _ensureFreshTokenAsync;
    private readonly ILogger? _logger;
    private readonly string? _sessionId;

    public string Name { get; private set; }
    public string Description { get; }
    public JsonObject Parameters { get; }
    public IReadOnlyList<string> Tags { get; }
    public bool Pinned => false;
    public bool RequiresConfirmation { get; }
    public TimeSpan Timeout { get; }
    public string Source => _server.Name;

    public ToolConnection Server => _server;
    public string RawToolName => _rawToolName;

    public McpTool(
        ToolConnection server,
        string rawToolName,
        string? title,
        string? description,
        JsonObject parameters,
        bool requiresConfirmation,
        TimeSpan timeout,
        Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, Task<CallToolResult>> callToolAsync,
        Func<CancellationToken, Task>? refreshTokenAsync = null,
        Func<CancellationToken, Task>? reconnectAsync = null,
        Func<string, CancellationToken, Task>? setStatusAsync = null,
        Func<CancellationToken, Task>? ensureFreshTokenAsync = null,
        ILogger? logger = null,
        string? sessionId = null)
    {
        _server = server;
        _rawToolName = rawToolName;
        _host = ParseHost(server.Url);
        _callToolAsync = callToolAsync;
        _refreshTokenAsync = refreshTokenAsync;
        _reconnectAsync = reconnectAsync;
        _setStatusAsync = setStatusAsync;
        _ensureFreshTokenAsync = ensureFreshTokenAsync;
        _logger = logger;
        _sessionId = sessionId;

        Name = FormatToolName(server.Slug, rawToolName);
        Description = FormatDescription(server.Name, title, description);
        Parameters = parameters;
        Tags = ExtractTags(server.Slug, server.Name, title, rawToolName);
        RequiresConfirmation = requiresConfirmation;
        Timeout = timeout;
    }

    public void SetName(string name)
    {
        Name = name;
    }

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        string outcome = "error";
        try
        {
            if (_ensureFreshTokenAsync != null)
            {
                await _ensureFreshTokenAsync(cancellationToken);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(Timeout);

            try
            {
                var dict = ToDictionary(arguments);
                var callResult = await _callToolAsync(_rawToolName, dict, timeoutCts.Token);
                var mapped = MapResult(callResult);
                outcome = mapped.Outcome;
                return mapped;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                outcome = "timeout";
                return ToolResult.Failure("timeout") with { Outcome = "timeout" };
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                outcome = "auth";
                if (!RequiresConfirmation)
                {
                    // Unconfirmed tool: refresh once and retry
                    try
                    {
                        if (_refreshTokenAsync != null)
                        {
                            await _refreshTokenAsync(timeoutCts.Token);
                            var dict = ToDictionary(arguments);
                            var retryResult = await _callToolAsync(_rawToolName, dict, timeoutCts.Token);
                            var mapped = MapResult(retryResult);
                            outcome = mapped.Outcome;
                            return mapped;
                        }
                    }
                    catch
                    {
                        if (_setStatusAsync != null)
                        {
                            await _setStatusAsync("needs_reconnect", cancellationToken);
                        }
                        outcome = "auth";
                        return ToolResult.Failure("auth") with { Outcome = "auth" };
                    }
                    return ToolResult.Failure("auth") with { Outcome = "auth" };
                }
                else
                {
                    // Confirmed tool: refresh so next request works, but DO NOT RETRY!
                    if (_refreshTokenAsync != null)
                    {
                        try { await _refreshTokenAsync(cancellationToken); }
                        catch
                        {
                            if (_setStatusAsync != null)
                            {
                                await _setStatusAsync("needs_reconnect", cancellationToken);
                            }
                        }
                    }
                    outcome = "auth";
                    return ToolResult.Failure("the server asked me to sign in again; please ask again") with { Outcome = "auth" };
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                if (!RequiresConfirmation)
                {
                    // Unconfirmed tool: reconnect once and retry
                    try
                    {
                        if (_reconnectAsync != null)
                        {
                            await _reconnectAsync(timeoutCts.Token);
                            var dict = ToDictionary(arguments);
                            var retryResult = await _callToolAsync(_rawToolName, dict, timeoutCts.Token);
                            var mapped = MapResult(retryResult);
                            outcome = mapped.Outcome;
                            return mapped;
                        }
                    }
                    catch
                    {
                        outcome = "unreachable";
                        return ToolResult.Failure("unreachable") with { Outcome = "unreachable" };
                    }
                    outcome = "unreachable";
                    return ToolResult.Failure("unreachable") with { Outcome = "unreachable" };
                }
                else
                {
                    // Confirmed tool: reconnect once so next request works, but DO NOT RETRY!
                    if (_reconnectAsync != null)
                    {
                        try { await _reconnectAsync(cancellationToken); }
                        catch { /* ignore */ }
                    }
                    outcome = "auth";
                    return ToolResult.Failure("the server asked me to sign in again; please ask again") with { Outcome = "auth" };
                }
            }
            catch (Exception)
            {
                outcome = "unreachable";
                return ToolResult.Failure("unreachable") with { Outcome = "unreachable" };
            }
        }
        finally
        {
            sw.Stop();
            _logger?.LogInformation("ToolCall {SessionId} {ServerId} {Host} {Tool} {DurationMs} {Outcome}",
                _sessionId ?? string.Empty, _server.Id, _host, Name, sw.ElapsedMilliseconds, outcome);
        }
    }

    private ToolResult MapResult(CallToolResult callResult)
    {
        bool ok = callResult.IsError != true;
        string outcome = ok ? "ok" : "error";

        JsonNode? structured = null;
        if (callResult.StructuredContent.HasValue)
        {
            try
            {
                structured = JsonNode.Parse(callResult.StructuredContent.Value.GetRawText());
            }
            catch { }
        }

        var textParts = new List<string>();
        if (callResult.Content != null)
        {
            foreach (var block in callResult.Content)
            {
                switch (block)
                {
                    case TextContentBlock tb:
                        if (!string.IsNullOrEmpty(tb.Text)) textParts.Add(tb.Text);
                        break;
                    case ImageContentBlock:
                        textParts.Add("[image omitted]");
                        break;
                    case AudioContentBlock:
                        textParts.Add("[audio omitted]");
                        break;
                    case ResourceLinkBlock rb:
                        textParts.Add($"[link: {rb.Name ?? rb.Title ?? rb.Uri}]");
                        break;
                    case EmbeddedResourceBlock eb:
                        if (eb.Resource is TextResourceContents tr && !string.IsNullOrEmpty(tr.Text))
                            textParts.Add(tr.Text);
                        else
                            textParts.Add("[resource omitted]");
                        break;
                }
            }
        }

        string joinedText = string.Join("\n", textParts);

        var dataNode = new JsonObject
        {
            ["source"] = Source,
            ["untrusted"] = true
        };
        if (structured != null)
        {
            dataNode["content"] = structured.DeepClone();
        }
        else
        {
            if (Encoding.UTF8.GetByteCount(joinedText) > 3800)
            {
                var bytes = Encoding.UTF8.GetByteCount(joinedText);
                var safeLen = Math.Min(joinedText.Length, 3500);
                dataNode["content"] = joinedText[..safeLen] + $" [truncated; original data size {bytes} bytes]";
            }
            else
            {
                dataNode["content"] = joinedText;
            }
        }

        string message = !string.IsNullOrEmpty(joinedText) ? joinedText : (ok ? "ok" : "error");

        return new ToolResult(ok, message, dataNode)
        {
            Outcome = outcome
        };
    }

    public static string FormatToolName(string slug, string toolName, HashSet<string>? existingNames = null)
    {
        var raw = $"{slug}__{toolName}";
        if (raw.Length <= 64 && (existingNames == null || !existingNames.Contains(raw)))
        {
            existingNames?.Add(raw);
            return raw;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..6].ToLowerInvariant();
        var maxPrefix = 64 - 1 - 6; // 57
        var prefix = raw.Length > maxPrefix ? raw[..maxPrefix] : raw;
        var name = $"{prefix}_{hash}";
        if (existingNames != null)
        {
            int counter = 1;
            while (existingNames.Contains(name))
            {
                var counterStr = counter.ToString();
                var p = raw.Length > (maxPrefix - counterStr.Length) ? raw[..(maxPrefix - counterStr.Length)] : raw;
                name = $"{p}{counterStr}_{hash}";
                counter++;
            }
            existingNames.Add(name);
        }
        return name;
    }

    public static string FormatDescription(string serverName, string? title, string? description)
    {
        var body = !string.IsNullOrWhiteSpace(description) ? description : (title ?? string.Empty);
        var formatted = string.IsNullOrWhiteSpace(body) ? $"[{serverName}]" : $"[{serverName}] {body}";
        if (formatted.Length > 1024)
        {
            formatted = formatted[..1024];
        }
        return formatted;
    }

    public static IReadOnlyList<string> ExtractTags(string slug, string serverName, string? title, string? toolName)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { slug.ToLowerInvariant() };
        AddWords(tags, serverName);
        AddWords(tags, title);
        AddWords(tags, toolName);
        return tags.ToList();
    }

    private static void AddWords(HashSet<string> tags, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var words = text.Split([' ', '\t', '\r', '\n', '-', '_', '.', '/', ':', ';', ',', '!', '?', '(', ')', '[', ']'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var w in words)
        {
            if (w.Length > 0) tags.Add(w.ToLowerInvariant());
        }
    }

    public static bool CalculateRequiresConfirmation(bool? readOnlyHint, bool serverAlwaysAsk, bool overrideAlwaysAsk)
    {
        return (readOnlyHint != true) || serverAlwaysAsk || overrideAlwaysAsk;
    }

    private static IReadOnlyDictionary<string, object?> ToDictionary(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, object?>();
        }
        var dict = new Dictionary<string, object?>();
        foreach (var property in arguments.EnumerateObject())
        {
            dict[property.Name] = ConvertJsonElement(property.Value);
        }
        return dict;
    }

    private static object? ConvertJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.Clone()
    };

    private static string ParseHost(string url)
    {
        try
        {
            return new Uri(url).Host;
        }
        catch
        {
            return url;
        }
    }
}
