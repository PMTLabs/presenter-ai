using System.Text.Json.Nodes;

namespace PresenterAi.Application.Tools.External;

public sealed class SessionToolSet : IAsyncDisposable
{
    private readonly IAsyncDisposable? _disposable;

    public SessionToolSet(
        IReadOnlyList<ITool>? tools = null,
        IReadOnlyList<JsonObject>? hostedTools = null,
        IReadOnlyList<string>? notes = null,
        IAsyncDisposable? disposable = null)
    {
        Tools = tools ?? [];
        HostedTools = hostedTools ?? [];
        Notes = notes ?? [];
        _disposable = disposable;
    }

    public IReadOnlyList<ITool> Tools { get; }

    public IReadOnlyList<JsonObject> HostedTools { get; }

    public IReadOnlyList<string> Notes { get; }

    public ValueTask DisposeAsync() => _disposable?.DisposeAsync() ?? ValueTask.CompletedTask;
}
