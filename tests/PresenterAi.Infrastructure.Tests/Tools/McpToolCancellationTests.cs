using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using PresenterAi.Application.Tools.External;
using PresenterAi.Infrastructure.Tools.Mcp;
using Xunit;

namespace PresenterAi.Infrastructure.Tests.Tools;

public sealed class McpToolCancellationTests
{
    [Fact]
    public async Task Caller_cancellation_during_refresh_does_not_retry_or_mark_needs_reconnect()
    {
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var statuses = 0;
        var tool = Create(
            (_, _, _) =>
            {
                calls++;
                throw new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized);
            },
            refresh: async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); },
            status: (_, _) => { statuses++; return Task.CompletedTask; });
        using var arguments = JsonDocument.Parse("{}");
        var invocation = tool.InvokeAsync(arguments.RootElement, caller.Token);
        await entered.Task;
        caller.Cancel();
        Assert.Equal("cancelled", (await invocation).Outcome);
        Assert.Equal(1, calls);
        Assert.Equal(0, statuses);
    }

    [Fact]
    public async Task Caller_cancellation_during_reconnect_does_not_retry()
    {
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var statuses = 0;
        var tool = Create(
            (_, _, _) => { calls++; throw new HttpRequestException("missing", null, HttpStatusCode.NotFound); },
            reconnect: async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); },
            status: (_, _) => { statuses++; return Task.CompletedTask; });
        using var arguments = JsonDocument.Parse("{}");
        var invocation = tool.InvokeAsync(arguments.RootElement, caller.Token);
        await entered.Task;
        caller.Cancel();
        Assert.Equal("cancelled", (await invocation).Outcome);
        Assert.Equal(1, calls);
        Assert.Equal(0, statuses);
    }

    [Fact]
    public async Task Caller_cancellation_before_retry_returns_cancelled()
    {
        using var caller = new CancellationTokenSource();
        var calls = 0;
        var statuses = 0;
        var tool = Create(
            (_, _, _) =>
            {
                calls++;
                throw new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized);
            },
            refresh: _ => { caller.Cancel(); return Task.CompletedTask; },
            status: (_, _) => { statuses++; return Task.CompletedTask; });
        using var arguments = JsonDocument.Parse("{}");
        Assert.Equal("cancelled", (await tool.InvokeAsync(arguments.RootElement, caller.Token)).Outcome);
        Assert.Equal(1, calls);
        Assert.Equal(0, statuses);
    }

    private static McpTool Create(
        Func<string, IReadOnlyDictionary<string, object?>, CancellationToken,
            Task<ModelContextProtocol.Protocol.CallToolResult>> call,
        Func<CancellationToken, Task>? refresh = null,
        Func<CancellationToken, Task>? reconnect = null,
        Func<string, CancellationToken, Task>? status = null)
    {
        var now = DateTimeOffset.UtcNow;
        var server = new ToolConnection(Guid.NewGuid(), "owner", "Test", "test", "https://example.test/mcp",
            "none", "connected", null, false, now, now, null);
        return new McpTool(server, "lookup", "Lookup", "Lookup", new JsonObject { ["type"] = "object" },
            false, TimeSpan.FromSeconds(30), call, refresh, reconnect, status);
    }
}
