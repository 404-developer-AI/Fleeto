using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Infrastructure.Security;

/// <summary>Purposes with their own data key. Rotating one re-encrypts only that purpose.</summary>
public static class SecretPurposes
{
    public const string Settings = "settings";
    public const string License = "license";
    public const string Identity = "identity";

    public static readonly IReadOnlyList<string> All = [Settings, License, Identity];
}

/// <summary>The root key (KEK) of the instance, loaded from its secret file.</summary>
public sealed class RootKey
{
    public RootKey(byte[] key)
    {
        if (key.Length != AesGcmBox.KeySize)
        {
            throw new ArgumentException("The root key must be 32 bytes.", nameof(key));
        }

        Key = key;
        Id = KeyIds.For(key);
    }

    internal byte[] Key { get; }
    public string Id { get; }
}

/// <summary>
/// Envelope encryption (ARCHITECTURE.md §5): the root key wraps one data key per purpose, stored in DataKeys;
/// data keys encrypt the data with AES-256-GCM. Ciphertext layout (base64 for strings):
/// version (1 = 0x01) | data key id (16, Guid bytes) | nonce (12) | tag (16) | ciphertext.
/// Associated data = "purpose|associatedData", which binds a ciphertext to the purpose and to the row it belongs to.
/// </summary>
public sealed class EnvelopeSecretProtector : ISecretProtector
{
    private const byte FormatVersion = 1;
    private readonly RootKey _rootKey;
    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly ConcurrentDictionary<Guid, byte[]> _keysById = new();
    private readonly ConcurrentDictionary<string, Guid> _activeKeyByPurpose = new();

    public EnvelopeSecretProtector(RootKey rootKey, IFleetoDbContextFactory dbFactory)
    {
        _rootKey = rootKey;
        _dbFactory = dbFactory;
    }

    public string Protect(string purpose, string plaintext, string associatedData) =>
        Convert.ToBase64String(Protect(purpose, Encoding.UTF8.GetBytes(plaintext), associatedData));

    public string Unprotect(string purpose, string ciphertext, string associatedData) =>
        Encoding.UTF8.GetString(Unprotect(purpose, Convert.FromBase64String(ciphertext), associatedData));

    public byte[] Protect(string purpose, ReadOnlySpan<byte> plaintext, string associatedData)
    {
        var (keyId, key) = GetActiveKey(purpose);
        var sealedData = AesGcmBox.Seal(key, plaintext, BuildAssociatedData(purpose, associatedData));

        var output = new byte[1 + 16 + sealedData.Length];
        output[0] = FormatVersion;
        keyId.TryWriteBytes(output.AsSpan(1, 16));
        sealedData.CopyTo(output.AsSpan(17));
        return output;
    }

    public byte[] Unprotect(string purpose, ReadOnlySpan<byte> ciphertext, string associatedData)
    {
        if (ciphertext.Length < 17 || ciphertext[0] != FormatVersion)
        {
            throw new CryptographicException("Unknown ciphertext format.");
        }

        var keyId = new Guid(ciphertext.Slice(1, 16));
        var key = GetKeyById(keyId, purpose);
        return AesGcmBox.Open(key, ciphertext[17..], BuildAssociatedData(purpose, associatedData));
    }

    /// <summary>Creates a wrapped data key for a purpose. Used by <c>fleeto-tool migrate</c> only.</summary>
    public static DataKey CreateDataKey(RootKey rootKey, string purpose, DateTime now)
    {
        var id = Guid.NewGuid();
        var key = RandomNumberGenerator.GetBytes(AesGcmBox.KeySize);
        try
        {
            return new DataKey
            {
                Id = id,
                Purpose = purpose,
                WrappedKey = AesGcmBox.Seal(rootKey.Key, key, WrapAssociatedData(id, purpose)),
                RootKeyId = rootKey.Id,
                CreatedAt = now
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Rewraps a data key that was wrapped with the associated data label from before the rename to Fleeto (0.2.1). Returns false
    /// when the key already uses the current label. Run by <c>fleeto-tool migrate</c>; the data itself is untouched.
    /// </summary>
    public static bool UpgradeLegacyWrap(DataKey dataKey, RootKey rootKey)
    {
        try
        {
            CryptographicOperations.ZeroMemory(AesGcmBox.Open(rootKey.Key, dataKey.WrappedKey, WrapAssociatedData(dataKey.Id, dataKey.Purpose)));
            return false;
        }
        catch (CryptographicException)
        {
            // Not the current label: try the legacy one below.
        }

        var key = AesGcmBox.Open(rootKey.Key, dataKey.WrappedKey, WrapAssociatedData(dataKey.Id, dataKey.Purpose, LegacyNames.DataKeyWrapLabel));
        try
        {
            dataKey.WrappedKey = AesGcmBox.Seal(rootKey.Key, key, WrapAssociatedData(dataKey.Id, dataKey.Purpose));
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Re-wraps a data key under a new root key (root key rotation). The data itself is untouched.</summary>
    public static void Rewrap(DataKey dataKey, RootKey oldRootKey, RootKey newRootKey)
    {
        var key = AesGcmBox.Open(oldRootKey.Key, dataKey.WrappedKey, WrapAssociatedData(dataKey.Id, dataKey.Purpose));
        try
        {
            dataKey.WrappedKey = AesGcmBox.Seal(newRootKey.Key, key, WrapAssociatedData(dataKey.Id, dataKey.Purpose));
            dataKey.RootKeyId = newRootKey.Id;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private (Guid Id, byte[] Key) GetActiveKey(string purpose)
    {
        if (_activeKeyByPurpose.TryGetValue(purpose, out var cachedId) && _keysById.TryGetValue(cachedId, out var cachedKey))
        {
            return (cachedId, cachedKey);
        }

        using var db = _dbFactory.CreateSystem();
        var dataKey = db.DataKeys.AsNoTracking().SingleOrDefault(k => k.Purpose == purpose && k.RetiredAt == null)
            ?? throw new InvalidOperationException(
                $"No data key exists for '{purpose}'. Run 'fleeto-tool migrate' for this instance.");

        var key = Unwrap(dataKey);
        _keysById[dataKey.Id] = key;
        _activeKeyByPurpose[purpose] = dataKey.Id;
        return (dataKey.Id, key);
    }

    private byte[] GetKeyById(Guid id, string purpose)
    {
        if (_keysById.TryGetValue(id, out var cached))
        {
            return cached;
        }

        using var db = _dbFactory.CreateSystem();
        var dataKey = db.DataKeys.AsNoTracking().SingleOrDefault(k => k.Id == id)
            ?? throw new CryptographicException("The data key for this ciphertext does not exist.");

        if (dataKey.Purpose != purpose)
        {
            throw new CryptographicException("The ciphertext belongs to a different purpose.");
        }

        var key = Unwrap(dataKey);
        _keysById[id] = key;
        return key;
    }

    private byte[] Unwrap(DataKey dataKey)
    {
        if (dataKey.RootKeyId != _rootKey.Id)
        {
            throw new CryptographicException(
                $"Data key '{dataKey.Purpose}' is wrapped by root key {dataKey.RootKeyId}, but the loaded root key is {_rootKey.Id}. " +
                "Check that the root key file belongs to this instance.");
        }

        return AesGcmBox.Open(_rootKey.Key, dataKey.WrappedKey, WrapAssociatedData(dataKey.Id, dataKey.Purpose));
    }

    private static byte[] WrapAssociatedData(Guid id, string purpose, string label = "fleeto-dek") => Encoding.UTF8.GetBytes($"{label}|{id:N}|{purpose}");

    private static byte[] BuildAssociatedData(string purpose, string associatedData) =>
        Encoding.UTF8.GetBytes(purpose + "|" + associatedData);
}
