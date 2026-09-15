using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Fleeto.Infrastructure.Security;

/// <summary>
/// Backup file encryption (ARCHITECTURE.md §5, Backups). The VPS holds only the backup public key.
/// <para>Per file:</para>
/// <list type="number">
/// <item>Generate an ephemeral X25519 key pair.</item>
/// <item>X25519(ephemeral private, backup public) gives a shared secret; an all-zero result is rejected.</item>
/// <item>HKDF-SHA256 (salt "fleeto-backup-v1", info = ephemeral public || backup public) derives a 32-byte file key. Files written
/// before the rename to Fleeto (0.2.1) used the legacy salt; decryption tries it when the first chunk does not authenticate.</item>
/// <item>The file is encrypted in chunks of 1 MiB with AES-256-GCM.</item>
/// </list>
/// <para>Layout:</para>
/// <code>
/// header: magic "FLTBKP01" (8) | recipient key id (8, first 8 bytes of SHA-256 of the backup public key) | ephemeral public key (32)
/// chunk:  plaintext length (4, big-endian, at most 1 MiB) | ciphertext | tag (16)
/// </code>
/// Nonce per chunk: 88-bit big-endian chunk counter starting at 0, followed by one byte that is 1 for the last
/// chunk and 0 otherwise. The header is the associated data of every chunk. The file ends with exactly one
/// chunk flagged last (possibly empty); a missing last chunk means truncation and decryption fails.
/// Nonce reuse is impossible because every file, including a retried upload, gets a fresh ephemeral key and so
/// its own file key.
/// </summary>
public static class BackupCipher
{
    public const int ChunkSize = 1024 * 1024;
    private const int HeaderSize = 8 + 8 + 32;
    private static readonly byte[] Magic = "FLTBKP01"u8.ToArray();
    private static readonly byte[] Salt = "fleeto-backup-v1"u8.ToArray();
    private static readonly byte[] LegacySalt = Encoding.ASCII.GetBytes(LegacyNames.BackupSalt);

    public static (byte[] PrivateKey, byte[] PublicKey) GenerateKeyPair()
    {
        var privateKey = new X25519PrivateKeyParameters(new SecureRandom());
        return (privateKey.GetEncoded(), privateKey.GeneratePublicKey().GetEncoded());
    }

    public static Task EncryptAsync(Stream input, Stream output, byte[] recipientPublicKey, CancellationToken cancellationToken = default) =>
        EncryptAsync(input, output, recipientPublicKey, Salt, cancellationToken);

    /// <summary>Encryption with a given HKDF salt; tests use it to write a file as it was written before the rename to Fleeto.</summary>
    internal static async Task EncryptAsync(Stream input, Stream output, byte[] recipientPublicKey, byte[] salt, CancellationToken cancellationToken)
    {
        if (recipientPublicKey.Length != 32)
        {
            throw new ArgumentException("The backup public key must be a 32-byte X25519 key.", nameof(recipientPublicKey));
        }

        var ephemeral = new X25519PrivateKeyParameters(new SecureRandom());
        var ephemeralPublic = ephemeral.GeneratePublicKey().GetEncoded();
        var fileKey = DeriveFileKey(ephemeral, new X25519PublicKeyParameters(recipientPublicKey), ephemeralPublic, recipientPublicKey, salt);

        try
        {
            var header = new byte[HeaderSize];
            Magic.CopyTo(header, 0);
            SHA256.HashData(recipientPublicKey).AsSpan(0, 8).CopyTo(header.AsSpan(8));
            ephemeralPublic.CopyTo(header, 16);
            await output.WriteAsync(header, cancellationToken);

            using var aes = new AesGcm(fileKey, AesGcmBox.TagSize);
            var current = new byte[ChunkSize];
            var next = new byte[ChunkSize];
            var currentLength = await ReadFullAsync(input, current, cancellationToken);
            ulong counter = 0;

            while (true)
            {
                // Read ahead so we know whether the current chunk is the last one.
                var nextLength = currentLength == ChunkSize ? await ReadFullAsync(input, next, cancellationToken) : 0;
                var isLast = currentLength < ChunkSize || nextLength == 0;

                await WriteChunkAsync(aes, output, header, current.AsMemory(0, currentLength), counter, isLast, cancellationToken);

                if (isLast)
                {
                    break;
                }

                counter++;
                (current, next) = (next, current);
                currentLength = nextLength;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);
        }
    }

    public static async Task DecryptAsync(Stream input, Stream output, byte[] recipientPrivateKey, CancellationToken cancellationToken = default)
    {
        var header = new byte[HeaderSize];
        if (await ReadFullAsync(input, header, cancellationToken) != HeaderSize || !header.AsSpan(0, 8).SequenceEqual(Magic))
        {
            throw new CryptographicException("This is not a Fleeto backup file.");
        }

        var recipient = new X25519PrivateKeyParameters(recipientPrivateKey);
        var recipientPublic = recipient.GeneratePublicKey().GetEncoded();
        if (!SHA256.HashData(recipientPublic).AsSpan(0, 8).SequenceEqual(header.AsSpan(8, 8)))
        {
            throw new CryptographicException("This backup was encrypted for a different backup key.");
        }

        var ephemeralPublic = header.AsSpan(16, 32).ToArray();
        var fileKey = DeriveFileKey(recipient, new X25519PublicKeyParameters(ephemeralPublic), ephemeralPublic, recipientPublic, Salt);
        var legacyFileKey = DeriveFileKey(recipient, new X25519PublicKeyParameters(ephemeralPublic), ephemeralPublic, recipientPublic, LegacySalt);

        try
        {
            using var current = new AesGcm(fileKey, AesGcmBox.TagSize);
            using var legacy = new AesGcm(legacyFileKey, AesGcmBox.TagSize);
            var aes = current;
            var lengthBuffer = new byte[4];
            var ciphertext = new byte[ChunkSize];
            var plaintext = new byte[ChunkSize];
            var tag = new byte[AesGcmBox.TagSize];
            ulong counter = 0;

            while (true)
            {
                if (await ReadFullAsync(input, lengthBuffer, cancellationToken) != 4)
                {
                    throw new CryptographicException("The backup file is truncated: the last chunk is missing.");
                }

                var length = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);
                if (length is < 0 or > ChunkSize)
                {
                    throw new CryptographicException("The backup file is corrupt: invalid chunk length.");
                }

                if (await ReadFullAsync(input, ciphertext.AsMemory(0, length), cancellationToken) != length ||
                    await ReadFullAsync(input, tag, cancellationToken) != AesGcmBox.TagSize)
                {
                    throw new CryptographicException("The backup file is truncated.");
                }

                // A file written before the rename to Fleeto uses the legacy file key: decided on the first chunk, which authenticates with
                // exactly one key (the salts differ, so the keys do).
                if (counter == 0 &&
                    !TryDecrypt(current, header, ciphertext.AsSpan(0, length), tag, plaintext.AsSpan(0, length), counter, last: false) &&
                    !TryDecrypt(current, header, ciphertext.AsSpan(0, length), tag, plaintext.AsSpan(0, length), counter, last: true))
                {
                    aes = legacy;
                }

                // Try "not last" first; a full chunk is normally not last. Exactly one of the two nonces authenticates.
                var isLast = !TryDecrypt(aes, header, ciphertext.AsSpan(0, length), tag, plaintext.AsSpan(0, length), counter, last: false);
                if (isLast && !TryDecrypt(aes, header, ciphertext.AsSpan(0, length), tag, plaintext.AsSpan(0, length), counter, last: true))
                {
                    throw new CryptographicException("The backup file is corrupt or has been tampered with.");
                }

                await output.WriteAsync(plaintext.AsMemory(0, length), cancellationToken);

                if (isLast)
                {
                    if (input.ReadByte() != -1)
                    {
                        throw new CryptographicException("The backup file has data after its last chunk.");
                    }

                    return;
                }

                counter++;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);
            CryptographicOperations.ZeroMemory(legacyFileKey);
        }
    }

    private static byte[] DeriveFileKey(X25519PrivateKeyParameters privateKey, X25519PublicKeyParameters peer,
        byte[] ephemeralPublic, byte[] recipientPublic, byte[] salt)
    {
        var agreement = new X25519Agreement();
        agreement.Init(privateKey);
        var shared = new byte[agreement.AgreementSize];
        agreement.CalculateAgreement(peer, shared, 0);

        try
        {
            if (shared.All(b => b == 0))
            {
                throw new CryptographicException("Invalid X25519 public key.");
            }

            var info = new byte[64];
            ephemeralPublic.CopyTo(info, 0);
            recipientPublic.CopyTo(info, 32);
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt, info);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    private static async Task WriteChunkAsync(AesGcm aes, Stream output, byte[] header, ReadOnlyMemory<byte> plaintext,
        ulong counter, bool isLast, CancellationToken cancellationToken)
    {
        var buffer = new byte[4 + plaintext.Length + AesGcmBox.TagSize];
        BinaryPrimitives.WriteInt32BigEndian(buffer, plaintext.Length);
        aes.Encrypt(BuildNonce(counter, isLast), plaintext.Span, buffer.AsSpan(4, plaintext.Length),
            buffer.AsSpan(4 + plaintext.Length, AesGcmBox.TagSize), header);
        await output.WriteAsync(buffer, cancellationToken);
    }

    private static bool TryDecrypt(AesGcm aes, byte[] header, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag,
        Span<byte> plaintext, ulong counter, bool last)
    {
        try
        {
            aes.Decrypt(BuildNonce(counter, last), ciphertext, tag, plaintext, header);
            return true;
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }
    }

    /// <summary>88-bit big-endian counter (upper 3 bytes always zero here) followed by the last-chunk flag.</summary>
    internal static byte[] BuildNonce(ulong counter, bool isLast)
    {
        var nonce = new byte[AesGcmBox.NonceSize];
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(3, 8), counter);
        nonce[11] = isLast ? (byte)1 : (byte)0;
        return nonce;
    }

    private static async Task<int> ReadFullAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    public static string EncodePublicKey(byte[] publicKey) => "fleeto-backup-pub:" + Convert.ToBase64String(publicKey);

    public static string EncodePrivateKey(byte[] privateKey) => "fleeto-backup-key:" + Convert.ToBase64String(privateKey);

    /// <summary>Parses a backup public key as shown in Settings or produced by <c>fleeto-tool backup keygen</c>.</summary>
    public static bool TryDecodePublicKey(string? text, out byte[] publicKey)
    {
        publicKey = [];
        var prefix = PrefixOf(text, "fleeto-backup-pub:", LegacyNames.BackupPublicKeyPrefix);
        if (prefix is null)
        {
            return false;
        }

        try
        {
            publicKey = Convert.FromBase64String(text!.Trim()[prefix.Length..]);
            return publicKey.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static byte[] DecodePrivateKey(string text)
    {
        var prefix = PrefixOf(text, "fleeto-backup-key:", LegacyNames.BackupPrivateKeyPrefix)
            ?? throw new FormatException("The file does not contain a Fleeto backup private key.");
        var trimmed = text.Trim();
        var key = Convert.FromBase64String(trimmed[prefix.Length..]);
        return key.Length == 32 ? key : throw new FormatException("A backup private key is 32 bytes.");
    }

    internal static string Describe() => Encoding.ASCII.GetString(Magic);

    /// <summary>The prefix <paramref name="text"/> starts with: the current one or the one from before the rename to Fleeto.</summary>
    private static string? PrefixOf(string? text, string current, string legacy)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.StartsWith(current, StringComparison.Ordinal) ? current
            : trimmed.StartsWith(legacy, StringComparison.Ordinal) ? legacy
            : null;
    }
}
