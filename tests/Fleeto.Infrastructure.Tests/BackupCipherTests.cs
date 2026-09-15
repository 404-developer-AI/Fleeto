using System.Security.Cryptography;
using Fleeto.Infrastructure.Security;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Backup encryption (0.1.0, ARCHITECTURE.md §5 Backups): round trips at chunk boundaries, detects truncation,
/// tampering and the wrong key, and never reuses a file key (every encryption uses a fresh ephemeral key).
/// </summary>
public class BackupCipherTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(BackupCipher.ChunkSize - 1)]
    [InlineData(BackupCipher.ChunkSize)]
    [InlineData(BackupCipher.ChunkSize + 1)]
    [InlineData(3 * BackupCipher.ChunkSize + 12345)]
    public async Task Encrypted_backup_decrypts_to_the_original_bytes(int size)
    {
        var (privateKey, publicKey) = BackupCipher.GenerateKeyPair();
        var original = RandomNumberGenerator.GetBytes(size);

        var encrypted = await EncryptAsync(original, publicKey);
        var decrypted = await DecryptAsync(encrypted, privateKey);

        Assert.Equal(original, decrypted);
    }

    [Fact]
    public async Task Truncated_backup_is_detected()
    {
        var (privateKey, publicKey) = BackupCipher.GenerateKeyPair();
        var encrypted = await EncryptAsync(RandomNumberGenerator.GetBytes(2 * BackupCipher.ChunkSize + 10), publicKey);

        // Drop the final chunk (4-byte length + 10 bytes + 16-byte tag).
        var truncated = encrypted[..^30];

        await Assert.ThrowsAnyAsync<CryptographicException>(() => DecryptAsync(truncated, privateKey));
    }

    [Fact]
    public async Task Backup_cut_exactly_at_a_full_chunk_is_detected()
    {
        var (privateKey, publicKey) = BackupCipher.GenerateKeyPair();
        var encrypted = await EncryptAsync(RandomNumberGenerator.GetBytes(2 * BackupCipher.ChunkSize), publicKey);

        // Keep the header and the first full chunk only: that chunk is not flagged last.
        var cut = encrypted[..(48 + 4 + BackupCipher.ChunkSize + 16)];

        await Assert.ThrowsAnyAsync<CryptographicException>(() => DecryptAsync(cut, privateKey));
    }

    [Fact]
    public async Task Tampered_backup_is_detected()
    {
        var (privateKey, publicKey) = BackupCipher.GenerateKeyPair();
        var encrypted = await EncryptAsync(RandomNumberGenerator.GetBytes(5000), publicKey);
        encrypted[100] ^= 0x01;

        await Assert.ThrowsAnyAsync<CryptographicException>(() => DecryptAsync(encrypted, privateKey));
    }

    [Fact]
    public async Task Backup_for_another_key_is_refused()
    {
        var (_, publicKey) = BackupCipher.GenerateKeyPair();
        var (otherPrivateKey, _) = BackupCipher.GenerateKeyPair();
        var encrypted = await EncryptAsync(RandomNumberGenerator.GetBytes(100), publicKey);

        var error = await Assert.ThrowsAnyAsync<CryptographicException>(() => DecryptAsync(encrypted, otherPrivateKey));
        Assert.Contains("different backup key", error.Message);
    }

    [Fact]
    public async Task Encrypting_the_same_file_twice_uses_different_ephemeral_keys()
    {
        var (_, publicKey) = BackupCipher.GenerateKeyPair();
        var original = RandomNumberGenerator.GetBytes(1000);

        var first = await EncryptAsync(original, publicKey);
        var second = await EncryptAsync(original, publicKey);

        // Header: magic (8) | recipient key id (8) | ephemeral public key (32).
        Assert.Equal(first[..16], second[..16]);
        Assert.NotEqual(first[16..48], second[16..48]);
        Assert.NotEqual(first[48..], second[48..]);
    }

    [Fact]
    public void Nonce_is_an_88_bit_counter_followed_by_the_last_chunk_flag()
    {
        var nonce = BackupCipher.BuildNonce(0x0102, isLast: true);

        Assert.Equal(12, nonce.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01, 0x02, 0x01 }, nonce);
        Assert.Equal(0, BackupCipher.BuildNonce(0, isLast: false)[11]);
    }

    [Fact]
    public void Public_key_text_round_trips_and_rejects_garbage()
    {
        var (_, publicKey) = BackupCipher.GenerateKeyPair();

        Assert.True(BackupCipher.TryDecodePublicKey(BackupCipher.EncodePublicKey(publicKey), out var decoded));
        Assert.Equal(publicKey, decoded);
        Assert.False(BackupCipher.TryDecodePublicKey("fleeto-backup-pub:AAAA", out _));
        Assert.False(BackupCipher.TryDecodePublicKey("ssh-ed25519 AAAA", out _));
    }

    private static async Task<byte[]> EncryptAsync(byte[] plaintext, byte[] publicKey)
    {
        using var input = new MemoryStream(plaintext);
        using var output = new MemoryStream();
        await BackupCipher.EncryptAsync(input, output, publicKey);
        return output.ToArray();
    }

    private static async Task<byte[]> DecryptAsync(byte[] ciphertext, byte[] privateKey)
    {
        using var input = new MemoryStream(ciphertext);
        using var output = new MemoryStream();
        await BackupCipher.DecryptAsync(input, output, privateKey);
        return output.ToArray();
    }
}
