using System.Security.Cryptography;
using System.Text;

namespace Fleeto.Infrastructure.Security;

/// <summary>
/// The signer key (KEK) of the instance. Mounted only in fleeto-signer (and the tool). It encrypts the
/// instance signing key and the internal CA key, so no other container can use them.
/// </summary>
public sealed class SignerKey
{
    public SignerKey(byte[] key)
    {
        if (key.Length != AesGcmBox.KeySize)
        {
            throw new ArgumentException("The signer key must be 32 bytes.", nameof(key));
        }

        Key = key;
        Id = KeyIds.For(key);
    }

    internal byte[] Key { get; }
    public string Id { get; }

    /// <summary>Encrypts key material; <paramref name="context"/> binds it to its row, e.g. "InstanceSigningKeys|&lt;id&gt;".</summary>
    public byte[] Seal(ReadOnlySpan<byte> plaintext, string context) =>
        AesGcmBox.Seal(Key, plaintext, AssociatedData("fleeto-signer", context));

    public byte[] Open(ReadOnlySpan<byte> sealedData, string context) =>
        AesGcmBox.Open(Key, sealedData, AssociatedData("fleeto-signer", context));

    /// <summary>
    /// Key material sealed before the rename to Fleeto (0.2.1) carries the legacy label: returns it sealed with the current label, or
    /// null when it already uses it. Throws when it opens with neither, as <see cref="Open"/> does. Used by <c>fleeto-tool migrate</c>.
    /// </summary>
    public byte[]? UpgradeLegacySeal(ReadOnlySpan<byte> sealedData, string context)
    {
        try
        {
            CryptographicOperations.ZeroMemory(Open(sealedData, context));
            return null;
        }
        catch (CryptographicException)
        {
            // Not the current label: try the legacy one below.
        }

        var plaintext = AesGcmBox.Open(Key, sealedData, AssociatedData(LegacyNames.SignerKeyLabel, context));
        try
        {
            return Seal(plaintext, context);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] AssociatedData(string label, string context) => Encoding.UTF8.GetBytes(label + "|" + context);
}
