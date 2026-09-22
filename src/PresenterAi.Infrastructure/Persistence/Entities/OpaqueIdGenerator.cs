using System.Security.Cryptography;

namespace PresenterAi.Infrastructure.Persistence.Entities;

internal static class OpaqueIdGenerator
{
    public static string Create(string prefix)
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return $"{prefix}_{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }
}
