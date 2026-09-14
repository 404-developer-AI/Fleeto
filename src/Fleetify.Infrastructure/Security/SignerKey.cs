using System.Text;

namespace Fleetify.Infrastructure.Security;

/// <summary>
/// The signer key (KEK) of the instance. Mounted only in fleetify-signer (and the tool). It encrypts the
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

    private byte[] Key { get; }
    public string Id { get; }

    /// <summary>Encrypts key material; <paramref name="context"/> binds it to its row, e.g. "InstanceSigningKeys|&lt;id&gt;".</summary>
    public byte[] Seal(ReadOnlySpan<byte> plaintext, string context) =>
        AesGcmBox.Seal(Key, plaintext, Encoding.UTF8.GetBytes("fleetify-signer|" + context));

    public byte[] Open(ReadOnlySpan<byte> sealedData, string context) =>
        AesGcmBox.Open(Key, sealedData, Encoding.UTF8.GetBytes("fleetify-signer|" + context));
}
