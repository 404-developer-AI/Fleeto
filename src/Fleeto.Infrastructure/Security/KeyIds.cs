using System.Security.Cryptography;

namespace Fleeto.Infrastructure.Security;

public static class KeyIds
{
    /// <summary>First 16 lowercase hex characters of SHA-256 over the key bytes. Identifies, never reveals, a key.</summary>
    public static string For(ReadOnlySpan<byte> key) => Convert.ToHexStringLower(SHA256.HashData(key))[..16];

    /// <summary>Lowercase hex SHA-256 of arbitrary bytes, e.g. a certificate DER.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
