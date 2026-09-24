using System.Xml.Linq;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Options;

namespace Fleeto.Web.Security;

/// <summary>
/// Keeps the data protection key ring of web (the keys behind the authentication cookies and antiforgery tokens) encrypted at
/// rest (0.6.0): every key is sealed with a data key of the instance, which the root key wraps, like every other secret. Whoever
/// reads the volume of web without the root key cannot forge a session.
/// </summary>
public static class KeyRingProtection
{
    /// <summary>Binds a sealed key to the key ring, so a ciphertext of another secret cannot be passed off as one.</summary>
    internal const string AssociatedData = "DataProtectionKey";

    internal const string EncryptedElementName = "rootKeyEncrypted";

    private static readonly XName RequiresEncryption = XName.Get("requiresEncryption", "http://schemas.asp.net/2015/03/dataProtection");

    /// <summary>Seals every new key of the ring with the root key of the instance.</summary>
    public static IDataProtectionBuilder ProtectKeysWithRootKey(this IDataProtectionBuilder builder)
    {
        builder.Services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(services => new ConfigureOptions<KeyManagementOptions>(options =>
            options.XmlEncryptor = new RootKeyXmlEncryptor(services.GetRequiredService<ISecretProtector>())));
        return builder;
    }

    /// <summary>
    /// Revokes the keys that are still stored unencrypted (written before 0.6.0) and deletes their files, so no key readable
    /// without the root key is left or accepted. Data protection creates a new, sealed key on first use; whoever was signed in
    /// signs in again once. Returns the number of keys retired. Run at start, before the first request.
    /// </summary>
    public static int RetireUnencryptedKeys(IKeyManager keys, DirectoryInfo directory, ILogger logger)
    {
        if (!directory.Exists)
        {
            return 0;
        }

        var unencrypted = new List<(Guid Id, FileInfo File)>();
        foreach (var file in directory.EnumerateFiles("key-*.xml"))
        {
            var key = XElement.Load(file.FullName);
            // A secret that still carries requiresEncryption="true" was written as it is; a sealed one was replaced by its ciphertext.
            if (key.Descendants().Any(e => (bool?)e.Attribute(RequiresEncryption) == true) && Guid.TryParse((string?)key.Attribute("id"), out var id))
            {
                unencrypted.Add((id, file));
            }
        }

        foreach (var (id, file) in unencrypted)
        {
            keys.RevokeKey(id, "Stored unencrypted before 0.6.0; replaced by a key sealed with the root key.");
            file.Delete();
            logger.LogWarning("Retired data protection key {KeyId}: it was stored unencrypted. Users sign in again once", id);
        }

        return unencrypted.Count;
    }
}

/// <summary>Seals a key of the ring with <see cref="SecretPurposes.KeyRing"/>; see <see cref="KeyRingProtection"/>.</summary>
public sealed class RootKeyXmlEncryptor : IXmlEncryptor
{
    private readonly ISecretProtector _protector;

    public RootKeyXmlEncryptor(ISecretProtector protector)
    {
        _protector = protector;
    }

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var ciphertext = _protector.Protect(SecretPurposes.KeyRing, plaintextElement.ToString(SaveOptions.DisableFormatting), KeyRingProtection.AssociatedData);
        var element = new XElement(KeyRingProtection.EncryptedElementName,
            new XComment(" Sealed with a data key of the instance, which its root key wraps. "),
            new XElement("value", ciphertext));
        return new EncryptedXmlInfo(element, typeof(RootKeyXmlDecryptor));
    }
}

/// <summary>Opens a key sealed by <see cref="RootKeyXmlEncryptor"/>. Created by data protection, which passes the services.</summary>
public sealed class RootKeyXmlDecryptor : IXmlDecryptor
{
    private readonly ISecretProtector _protector;

    public RootKeyXmlDecryptor(IServiceProvider services)
    {
        _protector = services.GetRequiredService<ISecretProtector>();
    }

    public XElement Decrypt(XElement encryptedElement)
    {
        var ciphertext = (string?)encryptedElement.Element("value")
            ?? throw new System.Security.Cryptography.CryptographicException("The sealed data protection key has no value.");
        return XElement.Parse(_protector.Unprotect(SecretPurposes.KeyRing, ciphertext, KeyRingProtection.AssociatedData));
    }
}
