using System.Text.Json;

namespace PresenterAi.Application.Presenting;

public enum LiveSessionState
{
    Idle,
    Connecting,
    Open,
    Closing,
    Closed
}

public sealed record LiveSessionConfig(
    string Model,
    string Instructions,
    string Voice,
    string? PresentationTitle = null,
    IReadOnlyList<System.Text.Json.Nodes.JsonObject>? Tools = null);

public sealed record LiveSessionInfo(
    string? Id,
    string? Model,
    long? ExpiresAt,
    JsonElement Raw,
    string DelegationMode = "client");

public sealed record LiveCloseResult(string Reason, double? Seconds);
