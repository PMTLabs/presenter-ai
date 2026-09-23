using System.Text.Json;
using PresenterAi.Application.Tools.External;
using StackExchange.Redis;

namespace PresenterAi.Infrastructure.Tools.Mcp;

public sealed record McpOAuthState(string OwnerId, Guid ServerId, string Verifier, string Issuer,
    string TokenEndpoint, string ClientId, string? ClientSecret, string TokenAuthMethod,
    string Resource, string RedirectUri, string Registration, bool RequireIssuer);

public sealed class McpOAuthStateStore(IConnectionMultiplexer connection, CredentialProtector protector)
{
    private readonly IDatabase _database = connection.GetDatabase();

    public async Task SaveAsync(string state, McpOAuthState value)
    {
        var encrypted = protector.Protect(value.OwnerId, value.ServerId, JsonSerializer.Serialize(value));
        var frame = JsonSerializer.Serialize(new StateEnvelope(value.OwnerId, value.ServerId, encrypted.KeyId,
            Convert.ToBase64String(encrypted.Ciphertext)));
        await _database.StringSetAsync($"mcp:oauth:{state}", frame, TimeSpan.FromMinutes(10));
    }

    public async Task<McpOAuthState?> TakeAsync(string state)
    {
        if (string.IsNullOrWhiteSpace(state) || state.Length > 256) return null;
        var frame = await _database.StringGetDeleteAsync($"mcp:oauth:{state}");
        if (frame.IsNullOrEmpty) return null;
        try
        {
            var envelope = JsonSerializer.Deserialize<StateEnvelope>((string)frame!);
            if (envelope is null) return null;
            var result = protector.Unprotect(envelope.OwnerId, envelope.ServerId,
                new ToolCredential(Convert.FromBase64String(envelope.Ciphertext), envelope.KeyId, null, 0));
            return result.Payload is null ? null : JsonSerializer.Deserialize<McpOAuthState>(result.Payload);
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return null;
        }
    }

    private sealed record StateEnvelope(string OwnerId, Guid ServerId, string KeyId, string Ciphertext);
}
