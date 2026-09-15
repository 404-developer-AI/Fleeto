using System.Security.Cryptography;

namespace Fleeto.Infrastructure.Security;

/// <summary>
/// AES-256-GCM with a random 96-bit nonce per message. Output layout: nonce (12) | tag (16) | ciphertext.
/// The associated data is authenticated but not stored; decryption fails unless the same value is given.
/// </summary>
public static class AesGcmBox
{
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int KeySize = 32;

    public static byte[] Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException("AES-256-GCM needs a 32-byte key.", nameof(key));
        }

        var output = new byte[NonceSize + TagSize + plaintext.Length];
        var nonce = output.AsSpan(0, NonceSize);
        var tag = output.AsSpan(NonceSize, TagSize);
        var ciphertext = output.AsSpan(NonceSize + TagSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return output;
    }

    public static byte[] Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> sealedData, ReadOnlySpan<byte> associatedData)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException("AES-256-GCM needs a 32-byte key.", nameof(key));
        }

        if (sealedData.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Ciphertext is too short.");
        }

        var plaintext = new byte[sealedData.Length - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(sealedData[..NonceSize], sealedData[(NonceSize + TagSize)..], sealedData.Slice(NonceSize, TagSize),
            plaintext, associatedData);
        return plaintext;
    }
}
