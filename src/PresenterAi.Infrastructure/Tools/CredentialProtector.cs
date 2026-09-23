using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PresenterAi.Application.Tools.External;

namespace PresenterAi.Infrastructure.Tools;

public sealed record ProtectedCredential(byte[] Ciphertext, string KeyId);
public sealed record CredentialReadResult(string? Payload, string? ErrorCode);

public sealed class CredentialProtector
{
    private readonly byte[]? _key;
    public bool Available => _key is not null;
    public string? KeyId { get; }

    public CredentialProtector(IOptions<ExternalToolsOptions> options)
    {
        var configured = options.Value.CredentialKey;
        if (string.IsNullOrEmpty(configured))
        {
            return;
        }
        _key = Convert.FromBase64String(configured);
        if (_key.Length != 32)
        {
            throw new ArgumentException("Tools:CredentialKey must be 32 bytes");
        }
        KeyId = Convert.ToHexString(SHA256.HashData(_key)[..8]).ToLowerInvariant();
    }

    private static byte[] AssociatedData(string ownerId, Guid serverId) =>
        Encoding.UTF8.GetBytes($"tool-credential:v1:{ownerId}:{serverId}");

    public ProtectedCredential Protect(string ownerId, Guid serverId, string payload)
    {
        if (_key is null)
        {
            throw new InvalidOperationException("tools_credentials_unavailable");
        }
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(payload);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(_key, 16))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(ownerId, serverId));
        }
        var frame = new byte[2 + 12 + 16 + ciphertext.Length];
        frame[0] = (byte)'v';
        frame[1] = (byte)'1';
        nonce.CopyTo(frame.AsSpan(2));
        tag.CopyTo(frame.AsSpan(14));
        ciphertext.CopyTo(frame.AsSpan(30));
        CryptographicOperations.ZeroMemory(plaintext);
        return new ProtectedCredential(frame, KeyId!);
    }

    public CredentialReadResult Unprotect(string ownerId, Guid serverId, ToolCredential credential)
    {
        if (_key is null)
        {
            return new(null, "tools_credentials_unavailable");
        }
        if (credential.KeyId != KeyId)
        {
            return new(null, "credential_key_changed");
        }
        var frame = credential.Ciphertext;
        if (frame.Length < 30 || frame[0] != 'v' || frame[1] != '1')
        {
            return new(null, "credential_unreadable");
        }
        var plaintext = new byte[frame.Length - 30];
        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(frame.AsSpan(2, 12), frame.AsSpan(30), frame.AsSpan(14, 16), plaintext,
                AssociatedData(ownerId, serverId));
            return new(Encoding.UTF8.GetString(plaintext), null);
        }
        catch (CryptographicException)
        {
            return new(null, "credential_unreadable");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task<CredentialReadResult> ReadAsync(IToolConnectionRepository repository, string ownerId,
        Guid serverId, CancellationToken cancellationToken = default)
    {
        var credential = await repository.GetCredentialAsync(ownerId, serverId, cancellationToken);
        if (credential is null)
        {
            return new(null, null);
        }
        var result = Unprotect(ownerId, serverId, credential);
        if (result.ErrorCode is "credential_key_changed" or "credential_unreadable")
        {
            await repository.SetStatusAsync(ownerId, serverId, "needs_reconnect", result.ErrorCode, cancellationToken);
        }
        return result;
    }
}
