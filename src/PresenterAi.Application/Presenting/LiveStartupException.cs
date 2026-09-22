using System.Text.Json;

namespace PresenterAi.Application.Presenting;

public sealed class LiveStartupException : InvalidOperationException
{
    public LiveStartupException(string code, JsonElement error, string message)
        : base(message)
    {
        Code = code;
        Error = error.Clone();
    }

    public string Code { get; }

    public JsonElement Error { get; }
}
