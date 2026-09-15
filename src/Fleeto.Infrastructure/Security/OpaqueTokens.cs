using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Fleeto.Infrastructure.Security;

/// <summary>
/// Bearer tokens of the form <c>&lt;prefix&gt;_&lt;id&gt;_&lt;secret&gt;</c>: id is a Guid in "N" format, secret is 32 random bytes
/// (256 bits) base64url. Only the SHA-256 of the secret is stored; SHA-256 suffices because the input is long
/// and random. The fixed prefix lets secret scanners recognise a leaked token.
/// </summary>
public static class OpaqueTokens
{
    /// <summary>Agent enrollment tokens.</summary>
    public const string EnrollmentPrefix = "fet";

    /// <summary>First-admin setup links.</summary>
    public const string SetupPrefix = "fsu";

    /// <summary>Public API keys (0.2.0).</summary>
    public const string ApiKeyPrefix = "flt";

    public static (string Token, Guid Id, string SecretHash) Create(string prefix)
    {
        var id = Guid.NewGuid();
        var secret = RandomNumberGenerator.GetBytes(32);
        var secretText = Base64Url.EncodeToString(secret);
        return ($"{prefix}_{id:N}_{secretText}", id, HashSecret(secretText));
    }

    /// <summary>Splits a token. Returns false for anything that does not have the expected shape.</summary>
    public static bool TryParse(string? token, string expectedPrefix, out Guid id, out string secretHash)
    {
        id = Guid.Empty;
        secretHash = string.Empty;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200)
        {
            return false;
        }

        // At most three parts: the base64url secret itself may contain underscores.
        var parts = token.Trim().Split('_', 3);
        if (parts.Length != 3 || parts[0] != expectedPrefix || !Guid.TryParseExact(parts[1], "N", out id))
        {
            return false;
        }

        if (!Base64Url.IsValid(parts[2], out var decodedLength) || decodedLength != 32)
        {
            return false;
        }

        secretHash = HashSecret(parts[2]);
        return true;
    }

    private static string HashSecret(string secretText) => KeyIds.Sha256Hex(Encoding.ASCII.GetBytes(secretText));
}
